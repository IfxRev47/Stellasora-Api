package com.Star.cn;

import androidx.appcompat.app.AppCompatActivity;

import android.os.Bundle;
import android.widget.TextView;

import com.Star.cn.databinding.ActivityMainBinding;

/**
 * 模块 App 只作为「说明书」用：真正的登录页在游戏进程里，
 * 它的设置也存在游戏的 SharedPreferences 中，模块 App 读不到。
 */
public class MainActivity extends AppCompatActivity {

    private static final String GUIDE =
            "本地账号联调模块\n\n"
                    + "在 LSPosed 中选择「星塔旅人」并重新启动游戏。\n"
                    + "点击「登」浮窗后输入本地账号和密码，可直接登录或注册新账号。\n"
                    + "注册只创建本地账号；进入游戏后还需要创建角色，两步都有提示。\n"
                    + "长按浮窗可以修改本地服务器地址。\n\n"
                    + "当前入口用于验证本地账号接口，认证日志使用 ChxRe 标签。";

    private ActivityMainBinding binding;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        binding = ActivityMainBinding.inflate(getLayoutInflater());
        setContentView(binding.getRoot());

        TextView tv = binding.sampleText;
        tv.setText(GUIDE);
    }

}
