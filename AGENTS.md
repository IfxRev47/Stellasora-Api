# Agent 部署入口

1. 先读根目录 `README.md` 与 `LOCAL_FILES.md`。后端部署不依赖原始游戏资料，不要因为没有 APK、Lua、Hotfix 或 Dobby 而阻塞网关/GM。
2. 检查 `dotnet --version`（10.x）、`node --version`（22.13+，已验证 26）、`npm --version`。
3. 从根目录运行 `dotnet run --project server/Stella.LocalGateway`，等待 `/healthz` 返回 ok。首次自动建库。
4. 从 `gm/` 运行 `npm ci`、`npm run build`、`npm start`，访问 8787。
5. 默认仅本机运行，游戏密钥缺失不会阻止健康检查、账号 API 或 GM。仅在需要实际游戏联调时配置使用者提供的密钥和 LAN 地址。
6. 不要把实际 User Secrets、SQLite、令牌、日志、设备抓包、APK、游戏资源或构建缓存提交到 git。仓库中的测试账号/测试密钥示例不是私人凭据。
7. 核验改动用 `dotnet build server/Stella.LocalGateway`、`python server/Stella.LocalGateway/verify_protocol_gaps.py`、`cd gm && npm run build`。运行 Probe 写测试必须使用独立测试库；gm 模式会修改测试账号密码。
8. Windows 的 `server/start-local.ps1` 是可选后台启动器，停止用 `-StopOnly`。不要让 VS 和后台网关同时占用 1989。
9. 这是部分协议实现的本地测试项目。遇到新请求先查协议索引、proto 定义和实际调用日志，不要把空成功包视为完成业务实现。
