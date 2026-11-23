# Xposed 本地账号联调

模块在 Unity 主 Activity 挂载手动「登」浮窗。点击后打开本地账号窗口，长按浮窗可修改服务器地址。默认地址为 `http://127.0.0.1:1989`。

## 登录与注册

窗口里的账号、密码、确认密码共用一套输入，底部两个按钮：

- **进入游戏** → `POST /account-api/login`。成功后把本地票据交给游戏（优先 SDK `sessionLogin`，失败回退 `OnLoginNotify`）。
- **注册新账号** → `POST /account-api/register`。只创建本地账号，成功后在状态行显示 UID，并提示“请点击进入游戏，登录后需要在游戏内创建角色”。

服务端返回的每一步都会写在卡片内的状态行（失败时同时弹一次 Toast），不会自动关窗：

| 情况 | 界面反馈 |
| --- | --- |
| 注册成功 | 绿色：`注册成功（UID 10002）：请点击「进入游戏」，登录后需要在游戏内创建角色。` |
| 重复账号（409） | 红色：`该账号已存在，请直接登录` |
| 非法账号 / 短密码 | 输入框错误提示，不发请求 |
| 错误密码（401） | 红色：`本地账号或密码不正确` |
| 连接失败 / 超时 | 红色：`无法连接本地服务器：http://…，请检查服务器地址与网络` |
| 登录成功但无角色 | Toast：`登录成功（UID …），该账号还没有角色，请在游戏内创建角色` |
| 登录成功且已有角色 | Toast：`登录成功，正在进入游戏…` |

账号规则与服务端一致：3-32 位字母、数字、下划线或短横线；密码 8-128 位。客户端只做格式预检，真正的认证和 UID 分配都在本地服务端完成。

“注册账号”和“创建角色”是两步：模块负责第一步；第二步由游戏客户端在 `1001` 收到 `player_new_notify` 后打开创建角色界面，提交 `1004`，由网关写库并回 `1002`。

固定测试账号和服务端配置见 [本地网关 README](../server/Stella.LocalGateway/README.md)。

## 构建

在 xposed 目录使用已配置的 Android SDK；`JAVA_HOME` 指向任一 JDK 17+（本机可用 `C:\Users\<用户>\.gradle\jdks\eclipse_adoptium-21-amd64-windows.2`）。

```powershell
.\gradlew.bat :app:assembleDebug
```

产物位于 `app/build/outputs/apk/debug/app-debug.apk`。

## 日志

过滤 `ChxRe` 标签：`submitCredentials` / `submitRegistration` 记录账号与服务端地址（不记录密码），`account http status=` 记录接口状态码。
