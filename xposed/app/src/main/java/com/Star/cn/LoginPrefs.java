package com.Star.cn;

import android.content.Context;
import android.content.SharedPreferences;



/**
 * 登录页的本地设置。
 *
 * 存在游戏自己的 SharedPreferences 里（hook 跑在游戏进程里，写模块自己的沙盒没有权限），
 * 所以「恢复原版」之后要重新开启，靠游戏里那个小圆点，不依赖模块 App。
 */
final class LoginPrefs {

    private static final String FILE = "chx_login";
    private static final String KEY_SERVER = "server";
    private static final String KEY_ACCOUNT = "last_account";
    private static final String KEY_OVERRIDE = "override_ui";
    private static final String KEY_DOT_X = "dot_x";
    private static final String KEY_DOT_Y = "dot_y";
    private static final String KEY_SHOW_DOT = "show_manual_login_dot";

    static final String DEFAULT_SERVER = "http://127.0.0.1:1989";

    private final SharedPreferences sp;

    LoginPrefs(Context context) {
        sp = context.getApplicationContext().getSharedPreferences(FILE, Context.MODE_PRIVATE);
    }

    String server() {
        String value = sp.getString(KEY_SERVER, DEFAULT_SERVER);
        return value == null || value.trim().isEmpty() ? DEFAULT_SERVER : value.trim();
    }

    void server(String url) {
        sp.edit().putString(KEY_SERVER, url == null ? DEFAULT_SERVER : url.trim()).apply();
    }

    String lastAccount() {
        return sp.getString(KEY_ACCOUNT, "");
    }

    void rememberAccount(String account) {
        sp.edit().putString(KEY_ACCOUNT, account).apply();
    }

    /** true = 接管悠星 SDK 登录入口；false = 恢复原版 SDK 登录流程。 */
    boolean overrideUi() {
        return sp.getBoolean(KEY_OVERRIDE, true);
    }

    void overrideUi(boolean enabled) {
        sp.edit().putBoolean(KEY_OVERRIDE, enabled).apply();
    }

    /** 浮窗位置（像素，-1 表示还没拖过，用默认位置）。 */
    int dotX() {
        return sp.getInt(KEY_DOT_X, -1);
    }

    int dotY() {
        return sp.getInt(KEY_DOT_Y, -1);
    }

    void dotPosition(int x, int y) {
        sp.edit().putInt(KEY_DOT_X, Math.max(0, x)).putInt(KEY_DOT_Y, Math.max(0, y)).apply();
    }

    /** 游戏内的手动测试入口默认显示。 */
    boolean showDot() {
        return sp.getBoolean(KEY_SHOW_DOT, true);
    }

    void showDot(boolean show) {
        sp.edit().putBoolean(KEY_SHOW_DOT, show).apply();
    }

}
