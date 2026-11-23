# 可选分析工具

这些脚本不是部署网关/GM 的前置步骤。仓库保留 `tools/` 与消息号索引，原始游戏输入、反编译输出和实际密钥均未发布。

运行前查看根目录 [LOCAL_FILES.md](../LOCAL_FILES.md) 的第五节，再用相应脚本 `--help` 查看参数。

```powershell
python -m pip install -r reverse/requirements.txt
python server/Stella.LocalGateway/verify_protocol_gaps.py
```

归档工具从 `STELLA_ARCHIVE_KEY` 读取匹配客户端的归档密钥；区服解码工具从 `STELLA_META_KEY` 或 `--key` 读取密钥，未提供时会报配置错误，不包含内置实际客户端密钥。unluac JAR 和 Dobby 静态库需自行取得。
