# 图吧工具箱手机助手（android-tuba-remote）

原生 Android（Kotlin + Jetpack Compose + Material 3）。通过局域网连接电脑端「内置工具 → 连接手机」：

- 连接方式：扫二维码（`tubalink://IP:端口?code=配对码`）/ 手动输入 IP + 6 位配对码
- 主界面：电脑配置（硬件信息，同电脑端「硬件信息」）+ 电脑屏幕截图（手动刷新）
- 小工具：实时硬件监控（自选监控项与曲线图，含 FPS）、PowerShell 终端、winget 应用安装、传输助手（文字 / 图片 / 文件互传）

协议为电脑端 `PhoneLinkService` 提供的 HTTP + JSON 接口（`X-Token` 鉴权，配对码换取令牌）。仅限可信局域网使用。

构建：`./gradlew assembleDebug test`（需要 Android SDK；CI：`.github/workflows/android-build.yml`）。
