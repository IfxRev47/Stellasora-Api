package com.Star.cn;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.text.InputType;
import android.util.Log;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewConfiguration;
import android.view.ViewGroup;
import android.view.inputmethod.EditorInfo;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.lang.ref.WeakReference;
import java.lang.reflect.Method;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

import de.robv.android.xposed.IXposedHookLoadPackage;
import de.robv.android.xposed.XC_MethodHook;
import de.robv.android.xposed.XposedHelpers;
import de.robv.android.xposed.callbacks.XC_LoadPackage;

/**
 * 登录引导层：与 Ba_Reverse 的模块保持同一条链路。
 *
 * 1. 改写 SDKConfigManager.getSdkUrl，把悠星 SDK 请求指向本地服务端；
 * 2. native 层改写 Unity 的 ServerRootURL，把游戏协议请求也指向本地服务端；
 * 3. LoginManager.login / sessionLogin 被自定义全屏页接管；
 * 4. 本地账号换到 UID + 票据后，先走 SDK 自己的 sessionLogin
 *    （/user/quick-login → OnLoginNotify）；SDK 回调失败时回退到直接
 *    UnitySendMessage("TechSDKBridge", "CallBack", OnLoginNotify)，
 *    保证票据一定进入游戏的 Lua 登录链。
 */
public class MainHook implements IXposedHookLoadPackage {
    private static final String TAG = "ChxRe";
    private static final String GAME_PKG = "com.RoamingStar.StellaSora";
    private static final String BRIDGE_GO = "TechSDKBridge";
    private static final String BRIDGE_METHOD = "CallBack";
    private static final String TAG_LOGIN = "CHX_LOGIN_SCREEN";
    private static final String TAG_DOT = "CHX_LOGIN_DOT";

    private static WeakReference<Activity> sActivity = new WeakReference<>(null);
    private static LoginPrefs sPrefs;
    private static LoginScreen sScreen;
    private static View sOverlay;
    private static View sDot;
    /** 本次进程内用户关掉了浮窗（下次进游戏会重新出现）。 */
    private static boolean sDotClosed;
    /** 本次进程里已经成功把票据交给游戏。 */
    private static boolean sLoggedIn;
    /** 反射调用 sessionLogin 期间为 true，避免自己的钩子再次拦截。 */
    private static boolean sSdkDispatching;
    /** 等待 SDK 的 OnLoginNotify；失败时用它触发直接回调。 */
    private static volatile boolean sAwaitingSdkCallback;
    private static volatile String sPendingUid;
    private static volatile String sPendingToken;

    @Override
    public void handleLoadPackage(XC_LoadPackage.LoadPackageParam lpparam) {
        if (!lpparam.packageName.equals(GAME_PKG)) return;
        System.loadLibrary("cn");
        hookSdkUrl(lpparam);
        hookLocalSdkAuthorization(lpparam.classLoader);
        hookSdkLogin(lpparam);
        hookSdkPay(lpparam);
        hookUnityActivity(lpparam);
    }

    private static void hookLocalSdkAuthorization(ClassLoader loader) {
        try {
            XposedHelpers.findAndHookMethod("com.airi.official.core.http.net.NetClientManager",
                    loader, "getHeaderAuth", long.class, String.class, String.class, new XC_MethodHook() {
                        @Override
                        protected void beforeHookedMethod(MethodHookParam param) {
                            String token = (String) param.args[1];
                            // 票据由本地网关签发；非空就补 Bearer，SDK 的 quick-login 会读取它。
                            if (token != null && !token.isEmpty()) param.setResult("Bearer " + token);
                        }
                    });
        } catch (Throwable t) {
            Log.w(TAG, "getHeaderAuth hook unavailable", t);
        }
    }

    // ---------- SDK 服务器地址 ----------
    private void hookSdkUrl(XC_LoadPackage.LoadPackageParam lpparam) {
        XposedHelpers.findAndHookMethod(
                "com.airi.common.services.SDKConfigManager",
                lpparam.classLoader,
                "getSdkUrl",
                new XC_MethodHook() {
                    @Override
                    protected void afterHookedMethod(MethodHookParam param) {
                        String url = serverUrl();
                        Log.d(TAG, "getSdkUrl " + param.getResult() + " -> " + url);
                        param.setResult(url);
                    }
                }
        );
    }

    private static String serverUrl() {
        try {
            Context context = currentActivity();
            if (context == null) context = appContext();
            if (context != null) {
                if (sPrefs == null) sPrefs = new LoginPrefs(context);
                return sPrefs.server();
            }
        } catch (Throwable t) {
            Log.w(TAG, "serverUrl fail", t);
        }
        return LoginPrefs.DEFAULT_SERVER;
    }

    // ---------- 接管 SDK 登录入口 ----------
    /**
     * LoginManager.login() / sessionLogin() 是悠星 SDK 弹登录界面的唯一入口。
     * 与 BA 模块相同：拦截这两个入口，弹出本地账号页；提交成功后再反射调用
     * sessionLogin，并放行这一次调用（sSdkDispatching）。
     */
    private void hookSdkLogin(XC_LoadPackage.LoadPackageParam lpparam) {
        Class<?> manager;
        try {
            manager = XposedHelpers.findClass(
                    "com.airi.official.core.manager.LoginManager", lpparam.classLoader);
        } catch (Throwable t) {
            Log.w(TAG, "LoginManager not found, sdk login hook skipped", t);
            return;
        }
        XposedHelpers.findAndHookMethod(manager, "login", new XC_MethodHook() {
            @Override
            protected void beforeHookedMethod(MethodHookParam param) {
                if (!takeOverSdkLogin()) return;
                Log.d(TAG, "intercept LoginManager.login() -> local login page");
                param.setResult(null);
                showLoginOnGameActivity(lpparam.classLoader);
            }
        });
        XposedHelpers.findAndHookMethod(manager, "sessionLogin",
                Activity.class, long.class, String.class, int.class, boolean.class,
                new XC_MethodHook() {
                    @Override
                    protected void beforeHookedMethod(MethodHookParam param) {
                        if (sSdkDispatching || !takeOverSdkLogin()) return;
                        Log.d(TAG, "intercept LoginManager.sessionLogin(uid="
                                + param.args[1] + ") -> local login page");
                        param.setResult(null);
                        showLoginOnGameActivity(lpparam.classLoader);
                    }
                });

        try {
            XposedHelpers.findAndHookMethod("com.unity3d.player.UnityPlayer", lpparam.classLoader,
                    "UnitySendMessage", String.class, String.class, String.class, new XC_MethodHook() {
                        @Override
                        protected void beforeHookedMethod(MethodHookParam param) {
                            if (!BRIDGE_GO.equals(param.args[0]) || !BRIDGE_METHOD.equals(param.args[1])) {
                                return;
                            }
                            try {
                                JSONObject payload = new JSONObject((String) param.args[2]);
                                if (!"OnLoginNotify".equals(payload.optString("METHOD"))) return;
                                int code = payload.optInt("R_CODE", -1);
                                Log.d(TAG, "Unity OnLoginNotify code=" + code);
                                if (code == 0) {
                                    sAwaitingSdkCallback = false;
                                    sLoggedIn = true;
                                } else if (sAwaitingSdkCallback) {
                                    // SDK 的 /user/quick-login 不可用时，把同一张本地票据
                                    // 直接交给游戏的 OnLoginNotify，后续仍由游戏自己发 IKE/LoginReq。
                                    sAwaitingSdkCallback = false;
                                    Activity act = currentActivity();
                                    if (act != null && sPendingUid != null && sPendingToken != null) {
                                        Log.w(TAG, "SDK login failed, fallback to local OnLoginNotify");
                                        sendFakeLogin(act, sPendingUid, sPendingToken);
                                    }
                                }
                            } catch (Exception ignored) {
                                // 其他 bridge 消息不一定是 JSON。
                            }
                        }
                    });
        } catch (Throwable t) {
            Log.w(TAG, "Unity login callback observer unavailable", t);
        }
    }

    private static boolean takeOverSdkLogin() {
        if (sPrefs == null) {
            Context context = currentActivity();
            if (context == null) context = appContext();
            if (context == null) return false;
            sPrefs = new LoginPrefs(context);
        }
        return sPrefs.overrideUi();
    }

    private static void showLoginOnGameActivity(ClassLoader classLoader) {
        try {
            Activity game = gameActivity(classLoader);
            Activity target = game != null ? game : currentActivity();
            if (target == null || target.getWindow() == null) return;
            if (sPrefs == null) sPrefs = new LoginPrefs(target);
            ViewGroup decor = (ViewGroup) target.getWindow().getDecorView();
            if (decor.findViewWithTag(TAG_LOGIN) != null) return;
            showLogin(target, decor);
        } catch (Throwable t) {
            Log.e(TAG, "showLoginOnGameActivity fail", t);
        }
    }

    private static Activity gameActivity(ClassLoader classLoader) {
        try {
            Object manager = XposedHelpers.callStaticMethod(
                    XposedHelpers.findClass("com.airi.common.services.SDKActivityManager", classLoader),
                    "getInstance");
            Object activity = XposedHelpers.callMethod(manager, "getGameActivity");
            return activity instanceof Activity ? (Activity) activity : null;
        } catch (Throwable t) {
            return null;
        }
    }

    /** 与 BA 模块相同的 SDK 会话登录入口。 */
    private static boolean sdkSessionLogin(Activity act, String uid, String token) {
        try {
            long uidValue = Long.parseLong(uid);
            Class<?> manager = Class.forName("com.airi.official.core.manager.LoginManager",
                    true, act.getClassLoader());
            Object instance = XposedHelpers.callStaticMethod(manager, "getInstance");
            Activity game = gameActivity(act.getClassLoader());
            Activity target = game != null ? game : act;
            sSdkDispatching = true;
            try {
                XposedHelpers.callMethod(instance, "sessionLogin",
                        target, uidValue, token, 1, true);
            } finally {
                sSdkDispatching = false;
            }
            Log.d(TAG, "sdk sessionLogin dispatched uid=" + uid);
            return true;
        } catch (Throwable t) {
            Log.e(TAG, "sdkSessionLogin fail", t);
            return false;
        }
    }

    // ---------- 注入到 Unity 主 Activity ----------
    private void hookUnityActivity(XC_LoadPackage.LoadPackageParam lpparam) {
        XposedHelpers.findAndHookMethod(
                "com.tech.sdk.bridge.TechSDKUnityPlayerActivity",
                lpparam.classLoader,
                "onResume",
                new XC_MethodHook() {
                    @Override
                    protected void afterHookedMethod(MethodHookParam param) {
                        Activity act = (Activity) param.thisObject;
                        sActivity = new WeakReference<>(act);
                        try {
                            ensureViews(act);
                        } catch (Throwable t) {
                            Log.e(TAG, "ensureViews fail", t);
                        }
                    }
                }
        );
    }

    private static void ensureViews(Activity act) {
        if (act.getWindow() == null) return;
        View decorView = act.getWindow().getDecorView();
        if (!(decorView instanceof ViewGroup)) return;
        ViewGroup decor = (ViewGroup) decorView;

        if (sPrefs == null) sPrefs = new LoginPrefs(act);

        View existingOverlay = decor.findViewWithTag(TAG_LOGIN);
        if (existingOverlay != null) {
            sOverlay = existingOverlay;
            return;
        }
        sOverlay = null;
        sScreen = null;

        // 与原版 SDK 登录并存的手动入口；真正的自动接管发生在 login/sessionLogin 钩子。
        View existingDot = decor.findViewWithTag(TAG_DOT);
        if (existingDot != null) {
            sDot = existingDot;
        } else {
            showDot(act, decor);
        }
    }

    // ---------- 全屏登录页 ----------
    private static void showLogin(final Activity act, final ViewGroup decor) {
        removeDot();
        try {
            sScreen = new LoginScreen(act, sPrefs, new LoginScreen.Listener() {
                @Override
                public void onLogin(String account, String password) {
                    submitCredentials(act, account, password);
                }

                @Override
                public void onRegister(String account, String password) {
                    submitRegistration(act, account, password);
                }

                @Override
                public void onClose() {
                    closeLogin(act);
                }
            });
            View root = sScreen.build();
            root.setTag(TAG_LOGIN);
            sOverlay = root;
            decor.addView(root, new ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
            Log.d(TAG, "login screen attached");
        } catch (Throwable t) {
            Log.e(TAG, "showLogin fail", t);
            sOverlay = null;
            sScreen = null;
        }
    }

    private static void closeLogin(Activity act) {
        if (sOverlay != null) {
            ViewGroup parent = (ViewGroup) sOverlay.getParent();
            if (parent != null) {
                try {
                    parent.removeView(sOverlay);
                } catch (Throwable t) {
                    Log.w(TAG, "remove overlay fail", t);
                }
            }
            sOverlay = null;
            sScreen = null;
        }
        if (act.getWindow() != null && act.getWindow().getDecorView() instanceof ViewGroup) {
            showDot(act, (ViewGroup) act.getWindow().getDecorView());
        }
    }

    // ---------- 登录动作 ----------
    private static void submitCredentials(Activity act, String account, String password) {
        final String server = sPrefs.server();
        final LoginScreen screen = sScreen;
        Log.d(TAG, "submitCredentials account=" + account + " server=" + server);
        new Thread(() -> {
            try {
                JSONObject response = postAccount(server, "/account-api/login", account, password);
                String userId = response.optString("uid");
                String ticket = response.optString("token");
                boolean hasCharacter = response.optBoolean("hasCharacter", true);
                Log.d(TAG, "account ticket uid=" + userId + " hasCharacter=" + hasCharacter
                        + " expiresIn=" + response.optInt("expiresIn", -1));
                if (userId.isEmpty() || ticket.isEmpty()) {
                    throw new IllegalStateException("服务端未返回 UID 或登录票据");
                }
                act.runOnUiThread(() -> {
                    if (sScreen != screen || act.isFinishing()) return;
                    screen.setBusy(false);
                    sPrefs.rememberAccount(account);
                    sLoggedIn = true;
                    sPendingUid = userId;
                    sPendingToken = ticket;
                    sAwaitingSdkCallback = true;
                    closeLogin(act);
                    if (!sdkSessionLogin(act, userId, ticket)) {
                        sAwaitingSdkCallback = false;
                        if (!sendFakeLogin(act, userId, ticket)) {
                            toast(act, "无法把登录票据交给游戏");
                            sLoggedIn = false;
                            return;
                        }
                    }
                    toast(act, hasCharacter
                            ? "登录成功，正在进入游戏…"
                            : "登录成功（UID " + userId + "），该账号还没有角色，请在游戏内创建角色");
                });
            } catch (Throwable error) {
                Log.e(TAG, "submitCredentials failed", error);
                act.runOnUiThread(() -> {
                    if (sScreen != screen || act.isFinishing()) return;
                    screen.setBusy(false);
                    screen.setStatus(messageOf(error), true);
                    toast(act, messageOf(error));
                });
            }
        }, "chx-account").start();
    }

    /**
     * 注册本地账号。注册只建账号（服务端返回 UID），角色仍由游戏内的
     * player_reg(1004) 创建；两步的进度都写回状态行，避免用户误以为注册完就已开局。
     */
    private static void submitRegistration(Activity act, String account, String password) {
        final String server = sPrefs.server();
        final LoginScreen screen = sScreen;
        Log.d(TAG, "submitRegistration account=" + account + " server=" + server);
        new Thread(() -> {
            try {
                JSONObject response = postAccount(server, "/account-api/register", account, password);
                String userId = response.optString("uid");
                Log.d(TAG, "account registered uid=" + userId);
                act.runOnUiThread(() -> {
                    if (sScreen != screen || act.isFinishing()) return;
                    screen.setBusy(false);
                    sPrefs.rememberAccount(account);
                    screen.setStatus("注册成功（UID " + userId + "）：请点击「进入游戏」，"
                            + "登录后需要在游戏内创建角色。", false);
                    toast(act, "注册成功：UID " + userId + "，请登录后创建角色");
                });
            } catch (Throwable error) {
                Log.e(TAG, "submitRegistration failed", error);
                act.runOnUiThread(() -> {
                    if (sScreen != screen || act.isFinishing()) return;
                    screen.setBusy(false);
                    String message = messageOf(error);
                    screen.setStatus(message, true);
                    toast(act, message);
                });
            }
        }, "chx-register").start();
    }

    private static String messageOf(Throwable error) {
        String message = error.getMessage();
        return message == null || message.trim().isEmpty() ? "本地账号请求失败" : message;
    }

    private static JSONObject postAccount(String server, String path, String account, String password) throws Exception {
        String base = server.endsWith("/") ? server.substring(0, server.length() - 1) : server;
        URL endpoint = new URL(base + path);
        Log.d(TAG, "POST " + endpoint);
        HttpURLConnection connection;
        try {
            connection = (HttpURLConnection) endpoint.openConnection();
        } catch (Throwable error) {
            throw new IllegalStateException("无法连接本地服务器：" + base);
        }
        try {
            connection.setInstanceFollowRedirects(false);
            connection.setRequestMethod("POST");
            connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
            connection.setRequestProperty("Accept", "application/json");
            connection.setConnectTimeout(4000);
            connection.setReadTimeout(8000);
            connection.setDoOutput(true);
            JSONObject payload = new JSONObject().put("username", account).put("password", password);
            try (OutputStream stream = connection.getOutputStream()) {
                stream.write(payload.toString().getBytes(StandardCharsets.UTF_8));
            }
            int status = connection.getResponseCode();
            Log.d(TAG, "account http status=" + status + " path=" + path);
            boolean success = status / 100 == 2;
            InputStream stream = success ? connection.getInputStream() : connection.getErrorStream();
            String fallback = status == 409 ? "该账号已存在，请直接登录"
                    : "本地账号接口请求失败（" + status + "）";
            if (stream == null) throw new IllegalStateException(fallback);
            StringBuilder body = new StringBuilder();
            try (BufferedReader reader = new BufferedReader(new InputStreamReader(stream, StandardCharsets.UTF_8))) {
                for (String line; (line = reader.readLine()) != null; ) body.append(line);
            }
            if (!success) {
                String message = fallback;
                try {
                    message = new JSONObject(body.toString()).optString("error", fallback);
                } catch (org.json.JSONException ignored) {
                    // 非 JSON 错误保留 HTTP 状态码。
                }
                throw new IllegalStateException(message);
            }
            return new JSONObject(body.toString());
        } catch (java.io.IOException error) {
            Log.w(TAG, "account request io failure", error);
            throw new IllegalStateException("无法连接本地服务器：" + base + "，请检查服务器地址与网络");
        } finally {
            connection.disconnect();
        }
    }

    /** 与 BA 模块相同的兜底：直接把 OnLoginNotify 送进 TechSDKBridge。 */
    private static boolean sendFakeLogin(Activity act, String uid, String token) {
        String json = "{\"METHOD\":\"OnLoginNotify\",\"R_CODE\":0,\"R_MSG\":\"OK\","
                + "\"LOGIN_UID\":\"" + escape(uid) + "\","
                + "\"LOGIN_TOKEN\":\"" + escape(token) + "\","
                + "\"LOGIN_NAME\":\"" + escape(uid) + "\","
                + "\"LOGIN_PLATFORM\":1,"
                + "\"MOBILE\":\"\",\"APPLE_ID\":\"\","
                + "\"CHANNEL\":\"official\",\"SUB_CHANNEL\":\"\"}";
        try {
            Class<?> unityPlayer = Class.forName("com.unity3d.player.UnityPlayer",
                    true, act.getClassLoader());
            Method send = unityPlayer.getMethod("UnitySendMessage",
                    String.class, String.class, String.class);
            send.invoke(null, BRIDGE_GO, BRIDGE_METHOD, json);
            Log.d(TAG, "local OnLoginNotify dispatched uid=" + uid);
            return true;
        } catch (Throwable t) {
            Log.e(TAG, "UnitySendMessage fail", t);
            return false;
        }
    }

    // ---------- 劫持 SDK 支付 ----------
    private void hookSdkPay(XC_LoadPackage.LoadPackageParam lpparam) {
        try {
            Class<?> manager = XposedHelpers.findClass(
                    "com.airi.official.core.manager.SdkManager", lpparam.classLoader);
            XposedHelpers.findAndHookMethod(manager, "pay",
                    android.app.Activity.class, String.class, String.class, String.class,
                    new XC_MethodHook() {
                        @Override
                        protected void beforeHookedMethod(MethodHookParam param) {
                            Log.d(TAG, "hijack SdkManager.pay(" + param.args[1] + ") -> fake success");
                            fakePaySuccess(lpparam.classLoader, param.args);
                            param.setResult(null);
                        }
                    });
            Log.d(TAG, "SdkManager.pay hook installed");
        } catch (Throwable t) {
            Log.w(TAG, "SdkManager.pay hook skipped", t);
        }
    }

    private static void fakePaySuccess(ClassLoader cl, Object[] args) {
        try {
            Object payManager = XposedHelpers.callStaticMethod(
                    XposedHelpers.findClass("com.airi.official.core.manager.PayManager", cl),
                    "getInstance");

            Object payInfo = cl.loadClass("com.airi.common.model.bean.PayInfo").newInstance();
            XposedHelpers.callMethod(payInfo, "setGameProductID", args[1]);
            XposedHelpers.callMethod(payInfo, "setNotifyUrl", args[2]);
            XposedHelpers.callMethod(payInfo, "setExtraData", args[3]);
            XposedHelpers.callMethod(payInfo, "setPrice", 1);

            Object orderInfo = cl.loadClass("com.airi.common.model.bean.OrderInfo").newInstance();
            XposedHelpers.callMethod(orderInfo, "setOrderNo", "mock" + System.currentTimeMillis());
            XposedHelpers.callMethod(orderInfo, "setPayState", "1");
            XposedHelpers.callMethod(orderInfo, "setPayType", "mock");

            XposedHelpers.callMethod(payManager, "setPayInfo", payInfo);
            XposedHelpers.callMethod(payManager, "setOrderInfo", orderInfo);
            XposedHelpers.callMethod(payManager, "setPay", true);
            XposedHelpers.callMethod(payManager, "setCreateSDKOrder", false);
            XposedHelpers.callMethod(payManager, "paySuccess", "mock pay success", false);
            Log.d(TAG, "fake pay success dispatched");
        } catch (Throwable t) {
            Log.e(TAG, "fakePaySuccess fail", t);
        }
    }

    // ---------- 手动入口 ----------
    private static void showDot(final Activity act, final ViewGroup decor) {
        if (sDotClosed || sPrefs == null || !sPrefs.showDot() || sLoggedIn) return;
        removeDot();
        final int size = dp(act, 40);

        final TextView dot = new TextView(act);
        dot.setText("登");
        dot.setContentDescription("打开本地登录");
        dot.setTextColor(0xFFB7C6DE);
        dot.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12);
        dot.setGravity(Gravity.CENTER);
        dot.setTypeface(Typeface.DEFAULT_BOLD);
        GradientDrawable bg = new GradientDrawable();
        bg.setShape(GradientDrawable.OVAL);
        bg.setColor(0xFF1B2230);
        bg.setStroke(Math.max(1, dp(act, 1)), 0xFF33415A);
        dot.setBackground(bg);
        dot.setAlpha(0.85f);
        dot.setTag(TAG_DOT);
        dot.setClickable(true);

        final FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(size, size);
        lp.gravity = Gravity.TOP | Gravity.START;
        int savedX = sPrefs.dotX();
        int savedY = sPrefs.dotY();
        lp.leftMargin = savedX >= 0 ? Math.min(savedX, Math.max(0, decor.getWidth() - size)) : dp(act, 6);
        lp.topMargin = savedY >= 0 ? Math.min(savedY, Math.max(0, decor.getHeight() - size)) : dp(act, 140);
        dot.setLayoutParams(lp);

        final int slop = ViewConfiguration.get(act).getScaledTouchSlop();
        final float[] down = new float[2];
        final int[] start = new int[2];
        final boolean[] dragged = {false};
        dot.setOnTouchListener((v, event) -> {
            switch (event.getActionMasked()) {
                case MotionEvent.ACTION_DOWN:
                    down[0] = event.getRawX();
                    down[1] = event.getRawY();
                    start[0] = lp.leftMargin;
                    start[1] = lp.topMargin;
                    dragged[0] = false;
                    v.setAlpha(1f);
                    return true;
                case MotionEvent.ACTION_MOVE: {
                    float dx = event.getRawX() - down[0];
                    float dy = event.getRawY() - down[1];
                    if (Math.abs(dx) > slop || Math.abs(dy) > slop) dragged[0] = true;
                    if (!dragged[0]) return true;
                    int maxX = Math.max(0, decor.getWidth() - size);
                    int maxY = Math.max(0, decor.getHeight() - size);
                    lp.leftMargin = Math.min(maxX, Math.max(0, (int) (start[0] + dx)));
                    lp.topMargin = Math.min(maxY, Math.max(0, (int) (start[1] + dy)));
                    v.setLayoutParams(lp);
                    return true;
                }
                case MotionEvent.ACTION_UP:
                case MotionEvent.ACTION_CANCEL:
                    v.setAlpha(0.85f);
                    sPrefs.dotPosition(lp.leftMargin, lp.topMargin);
                    if (!dragged[0] && event.getActionMasked() == MotionEvent.ACTION_UP) v.performClick();
                    return true;
                default:
                    return false;
            }
        });
        dot.setOnClickListener(v -> showLogin(act, decor));
        dot.setOnLongClickListener(v -> {
            showDotMenu(act, decor);
            return true;
        });

        sDot = dot;
        decor.addView(dot, lp);
    }

    private static void removeDot() {
        if (sDot == null) return;
        ViewGroup parent = (ViewGroup) sDot.getParent();
        if (parent != null) {
            try {
                parent.removeView(sDot);
            } catch (Throwable ignored) {
            }
        }
        sDot = null;
    }

    private static void showDotMenu(final Activity act, final ViewGroup decor) {
        final String[] items = {"打开本地登录", "修改服务器地址", "恢复原版登录界面", "关闭浮窗"};
        new AlertDialog.Builder(act)
                .setTitle("登录入口")
                .setItems(items, (d, which) -> {
                    if (sPrefs == null) sPrefs = new LoginPrefs(act);
                    switch (which) {
                        case 0:
                            sPrefs.overrideUi(true);
                            showLogin(act, decor);
                            break;
                        case 1:
                            editServer(act);
                            break;
                        case 2:
                            sPrefs.overrideUi(false);
                            closeLogin(act);
                            toast(act, "已恢复原版登录界面");
                            break;
                        default:
                            sDotClosed = true;
                            removeDot();
                            break;
                    }
                })
                .show();
    }

    private static void editServer(final Activity act) {
        if (sPrefs == null) sPrefs = new LoginPrefs(act);
        final EditText input = new EditText(act);
        input.setText(sPrefs.server());
        input.setSelection(input.getText().length());
        input.setInputType(InputType.TYPE_TEXT_VARIATION_URI);
        input.setHint(LoginPrefs.DEFAULT_SERVER);
        input.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14);
        input.setImeOptions(EditorInfo.IME_ACTION_DONE);

        LinearLayout wrap = new LinearLayout(act);
        wrap.setPadding(dp(act, 20), dp(act, 8), dp(act, 20), 0);
        wrap.addView(input, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        new AlertDialog.Builder(act)
                .setTitle("服务器地址")
                .setView(wrap)
                .setNegativeButton("取消", null)
                .setPositiveButton("保存", (d, w) -> {
                    String url = input.getText().toString().trim();
                    if (url.isEmpty()) url = LoginPrefs.DEFAULT_SERVER;
                    if (!url.startsWith("http://") && !url.startsWith("https://")) url = "http://" + url;
                    sPrefs.server(url);
                    toast(act, "服务器地址已保存：" + url);
                })
                .show();
    }

    // ---------- 小工具 ----------
    private static Activity currentActivity() {
        return sActivity.get();
    }

    private static Context appContext() {
        try {
            Class<?> helper = XposedHelpers.findClass("android.app.AndroidAppHelper", null);
            Object app = XposedHelpers.callStaticMethod(helper, "currentApplication");
            if (app instanceof Context) return (Context) app;
        } catch (Throwable ignored) {
        }
        try {
            Class<?> thread = XposedHelpers.findClass("android.app.ActivityThread", null);
            Object app = XposedHelpers.callStaticMethod(thread, "currentApplication");
            if (app instanceof Context) return (Context) app;
        } catch (Throwable ignored) {
        }
        return null;
    }

    private static void toast(final Activity act, final String message) {
        try {
            act.runOnUiThread(() -> Toast.makeText(act, message, Toast.LENGTH_SHORT).show());
        } catch (Throwable ignored) {
        }
    }

    private static int dp(Context context, float value) {
        return Math.round(value * context.getResources().getDisplayMetrics().density);
    }

    private static String escape(String s) {
        return s == null ? "" : s.replace("\\", "\\\\").replace("\"", "\\\"");
    }
}
