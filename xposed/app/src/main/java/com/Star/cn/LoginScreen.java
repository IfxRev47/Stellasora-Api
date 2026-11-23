package com.Star.cn;

import android.app.Activity;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.text.InputType;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.animation.DecelerateInterpolator;
import android.view.inputmethod.EditorInfo;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

/**
 * 全屏登录界面（智能居中与滚动，底部留白充足，绝对不会被挡住）。
 *
 * 登录和注册共用账号、密码两个输入框；「确认密码」只在注册时校验。
 * 服务端返回的 UID、重复账号、错误密码、连接失败都显示在卡片内的状态行，
 * 不依赖 Toast，避免消息一闪而过。
 */
final class LoginScreen {

    interface Listener {
        void onLogin(String account, String password);
        void onRegister(String account, String password);
        void onClose();
    }

    private static final int C_BG_TOP = 0xB3060913;    // 半透明极夜蓝渐变
    private static final int C_BG_BOTTOM = 0xB3020408;
    private static final int C_CARD = 0xE6101622;      // 高级深色半透明卡片
    private static final int C_STROKE = 0xFF1F2C44;
    private static final int C_TEXT = 0xFFF0F5FF;
    private static final int C_TEXT_DIM = 0xFF8392B0;
    private static final int C_ACCENT = 0xFF2575FC;
    private static final int C_ACCENT_DEEP = 0xFF6A11CB;
    private static final int C_INPUT_BG = 0xFF080C14;
    private static final int C_INPUT_STROKE = 0xFF243454;
    private static final int C_OK = 0xFF4CC38A;
    private static final int C_ERR = 0xFFFF6B6B;

    private final Activity act;
    private final LoginPrefs prefs;
    private final Listener listener;

    private FrameLayout root;
    private ScrollView scrollView;
    private LinearLayout column;
    private EditText accountInput;
    private EditText passwordInput;
    private EditText confirmInput;
    private TextView loginButton;
    private TextView registerButton;
    private TextView statusView;
    private TextView closeButton;
    private TextView dotToggle;

    LoginScreen(Activity act, LoginPrefs prefs, Listener listener) {
        this.act = act;
        this.prefs = prefs;
        this.listener = listener;
    }

    View build() {
        root = new FrameLayout(act);
        GradientDrawable bg = new GradientDrawable(
                GradientDrawable.Orientation.TOP_BOTTOM, new int[]{C_BG_TOP, C_BG_BOTTOM});
        root.setBackground(bg);
        root.setClickable(true);
        root.setFocusable(true);
        root.setFocusableInTouchMode(true);

        // 1. 滚动视图：fillViewport 保证内容少时居中，内容多/键盘弹起时可自由滑动
        scrollView = new ScrollView(act);
        scrollView.setFillViewport(true);
        scrollView.setVerticalScrollBarEnabled(false);
        root.addView(scrollView, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        // 2. 外部容器：采用垂直居中，并给上下左右留出充足的安全边距
        LinearLayout outerContainer = new LinearLayout(act);
        outerContainer.setOrientation(LinearLayout.VERTICAL);
        outerContainer.setGravity(Gravity.CENTER_HORIZONTAL | Gravity.CENTER_VERTICAL);
        outerContainer.setPadding(dp(20), dp(40), dp(20), dp(40));
        scrollView.addView(outerContainer, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        // 3. 核心内容列（限定卡片及整体宽度为 340dp）
        column = new LinearLayout(act);
        column.setOrientation(LinearLayout.VERTICAL);
        column.setGravity(Gravity.CENTER_HORIZONTAL);

        LinearLayout.LayoutParams columnLp = new LinearLayout.LayoutParams(
                dp(340), ViewGroup.LayoutParams.WRAP_CONTENT);
        outerContainer.addView(column, columnLp);

        // 进场线性动画
        column.setAlpha(0f);
        column.setScaleX(0.92f);
        column.setScaleY(0.92f);
        column.animate()
                .alpha(1f)
                .scaleX(1f)
                .scaleY(1f)
                .setDuration(300)
                .setInterpolator(new DecelerateInterpolator())
                .start();

        // 顶部关闭按钮（浮于右上角）
        closeButton = new TextView(act);
        closeButton.setText("✕");
        closeButton.setTextColor(C_TEXT_DIM);
        closeButton.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14);
        closeButton.setGravity(Gravity.CENTER);
        closeButton.setClickable(true);
        closeButton.setBackground(round(0x15FFFFFF, 0x30FFFFFF, 14));
        closeButton.setOnClickListener(v -> listener.onClose());

        FrameLayout.LayoutParams closeLp = new FrameLayout.LayoutParams(dp(32), dp(32));
        closeLp.gravity = Gravity.TOP | Gravity.END;
        closeLp.topMargin = dp(16);
        closeLp.rightMargin = dp(16);
        root.addView(closeButton, closeLp);

        // 标题完全居中
        TextView titleView = brandText("StellaSora", 24, C_TEXT, Typeface.BOLD, 0.15f);
        titleView.setGravity(Gravity.CENTER);
        column.addView(titleView);

        TextView subTitleView = brandText("SHIKOKAN SYSTEM", 10, C_ACCENT, Typeface.BOLD, 0.25f);
        subTitleView.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams subLp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        subLp.topMargin = dp(2);
        column.addView(subTitleView, subLp);

        // 中间主卡片
        LinearLayout card = new LinearLayout(act);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(18), dp(20), dp(18), dp(16));
        card.setBackground(round(C_CARD, C_STROKE, 20));
        LinearLayout.LayoutParams cardLp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        cardLp.topMargin = dp(16);
        column.addView(card, cardLp);

        accountInput = input("请输入账号", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        accountInput.setText(prefs.lastAccount());
        card.addView(field("账号", accountInput));

        passwordInput = input("请输入密码", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        passwordInput.setSaveEnabled(false);
        card.addView(field("密码", passwordInput));

        confirmInput = input("注册时再次输入密码",
                InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        confirmInput.setSaveEnabled(false);
        card.addView(field("确认密码（仅注册需要）", confirmInput));

        loginButton = primaryButton("进入游戏");
        loginButton.setOnClickListener(v -> submit(false));
        LinearLayout.LayoutParams loginLp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, dp(44));
        loginLp.topMargin = dp(14);
        card.addView(loginButton, loginLp);

        registerButton = new TextView(act);
        registerButton.setText("注册新账号");
        registerButton.setTextColor(0xFF9CC2FF);
        registerButton.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14);
        registerButton.setGravity(Gravity.CENTER);
        registerButton.setClickable(true);
        registerButton.setBackground(round(0x00000000, 0xFF2B4A7A, 14));
        registerButton.setOnClickListener(v -> submit(true));
        LinearLayout.LayoutParams registerLp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, dp(42));
        registerLp.topMargin = dp(10);
        card.addView(registerButton, registerLp);

        statusView = brandText("登录后可进入大厅；新账号需要在游戏内创建角色。",
                11, C_TEXT_DIM, Typeface.NORMAL, 0.01f);
        statusView.setGravity(Gravity.CENTER);
        statusView.setLineSpacing(dp(2), 1f);
        statusView.setPadding(0, dp(10), 0, 0);
        card.addView(statusView);

        dotToggle = brandText("", 11, 0xFF5F6E8C, Typeface.NORMAL, 0.02f);
        dotToggle.setGravity(Gravity.CENTER);
        dotToggle.setPadding(dp(10), dp(4), dp(10), dp(4));
        dotToggle.setClickable(true);
        dotToggle.setOnClickListener(v -> {
            prefs.showDot(!prefs.showDot());
            refreshDotToggle();
        });
        LinearLayout.LayoutParams dotToggleLp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        dotToggleLp.gravity = Gravity.CENTER_HORIZONTAL;
        dotToggleLp.topMargin = dp(4);
        column.addView(dotToggle, dotToggleLp);
        refreshDotToggle();

        return root;
    }

    private void refreshDotToggle() {
        if (dotToggle == null) return;
        boolean on = prefs.showDot();
        dotToggle.setText(on ? "❖ 悬浮球：已开启（点此关闭）" : "❖ 悬浮球：已关闭（点此开启）");
        dotToggle.setTextColor(on ? 0xFF65B8FF : 0xFF5F6E8C);
    }

    void setBusy(boolean busy) {
        if (loginButton == null) return;
        loginButton.setEnabled(!busy);
        loginButton.setText(busy ? "请稍候…" : "进入游戏");
        loginButton.setAlpha(busy ? 0.6f : 1f);
        registerButton.setEnabled(!busy);
        registerButton.setAlpha(busy ? 0.6f : 1f);
    }

    /** 结果行：error=true 用红色，false 用绿色。 */
    void setStatus(String text, boolean error) {
        if (statusView == null) return;
        statusView.setText(text);
        statusView.setTextColor(error ? C_ERR : C_OK);
    }

    void setStatusNeutral(String text) {
        if (statusView == null) return;
        statusView.setText(text);
        statusView.setTextColor(C_TEXT_DIM);
    }

    String account() {
        return accountInput == null ? "" : accountInput.getText().toString().trim();
    }

    private void submit(boolean register) {
        if (loginButton == null || !loginButton.isEnabled()) return;
        String account = account();
        String password = passwordInput.getText().toString();
        String confirm = confirmInput.getText().toString();
        if (!account.matches("[a-zA-Z0-9_-]{3,32}")) {
            accountInput.setError("请输入 3 到 32 个字母、数字、下划线或短横线");
            return;
        }
        if (password.length() < 8 || password.length() > 128) {
            passwordInput.setError("请输入 8 到 128 个字符");
            return;
        }
        if (register && !password.equals(confirm)) {
            confirmInput.setError("两次输入的密码不一致");
            return;
        }
        setBusy(true);
        setStatusNeutral(register ? "正在注册…" : "正在校验账号…");
        if (register) {
            listener.onRegister(account, password);
        } else {
            listener.onLogin(account, password);
        }
    }

    private TextView primaryButton(String label) {
        TextView button = new TextView(act);
        button.setText(label);
        button.setTextColor(Color.WHITE);
        button.setTextSize(TypedValue.COMPLEX_UNIT_SP, 15);
        button.setTypeface(Typeface.DEFAULT_BOLD);
        button.setGravity(Gravity.CENTER);
        GradientDrawable background = new GradientDrawable(
                GradientDrawable.Orientation.LEFT_RIGHT, new int[]{C_ACCENT, C_ACCENT_DEEP});
        background.setCornerRadius(dp(14));
        button.setBackground(background);
        button.setClickable(true);
        return button;
    }

    private LinearLayout field(String title, EditText input) {
        LinearLayout field = new LinearLayout(act);
        field.setOrientation(LinearLayout.VERTICAL);
        field.setPadding(0, dp(6), 0, dp(4));
        field.addView(label(title));

        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, dp(40));
        params.topMargin = dp(4);
        field.addView(input, params);
        return field;
    }

    private EditText input(String hint, int inputType) {
        EditText input = new EditText(act);
        input.setInputType(inputType);
        input.setSingleLine(true);
        input.setImeOptions(EditorInfo.IME_ACTION_GO);
        input.setHint(hint);
        input.setTextColor(C_TEXT);
        input.setHintTextColor(0xFF425270);
        input.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14);
        input.setPadding(dp(12), 0, dp(12), 0);
        input.setBackground(round(C_INPUT_BG, C_INPUT_STROKE, 10));
        input.setOnEditorActionListener((v, actionId, event) -> {
            if (actionId != EditorInfo.IME_ACTION_GO) return false;
            submit(false);
            return true;
        });
        return input;
    }

    private TextView label(String text) {
        return brandText(text, 11, C_TEXT_DIM, Typeface.NORMAL, 0.04f);
    }

    private TextView brandText(String text, float sp, int color, int style, float letterSpacing) {
        TextView view = new TextView(act);
        view.setText(text);
        view.setTextColor(color);
        view.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        view.setTypeface(Typeface.DEFAULT, style);
        if (letterSpacing != 0f && android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.LOLLIPOP) {
            view.setLetterSpacing(letterSpacing);
        }
        return view;
    }

    private GradientDrawable round(int fill, int stroke, float radiusDp) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(fill);
        drawable.setCornerRadius(dp(radiusDp));
        if (stroke != 0) drawable.setStroke(Math.max(1, dp(1)), stroke);
        return drawable;
    }

    private int dp(float value) {
        return Math.round(value * act.getResources().getDisplayMetrics().density);
    }
}
