# Win Request

Win Request 是一个参考 Swell-SSH 工程结构实现的 WinUI 3 API 调试客户端。当前版本聚焦主要功能：

- Collection 集合与请求保存，支持新增、重命名、删除集合和请求，数据位于 `%AppData%\WinRequest\workspace.json`
- 工作区环境变量，支持在 URL、Query、Headers、Body、gRPC 方法中使用 `{{变量名}}`
- Swagger / OpenAPI JSON、YAML 导入，自动生成 HTTP 请求
- HTTP 请求发送，支持 Method、URL、Query、Headers、Body
- 请求列表快速搜索，支持按集合、名称、Method、URL、gRPC 方法过滤
- 请求复制，可快速基于已有请求创建变体
- 发送中支持取消，避免慢接口或长连接卡住工作流
- 响应摘要展示状态、耗时、响应体大小与 Content-Type
- WebSocket 请求发送，支持连接、发送文本消息并读取一次响应
- gRPC 请求管理与发送，执行层调用本机 `grpcurl`
- 请求 Body 与响应 Body 的 JSON 格式化展示
- 请求历史记录，保存最近 500 条，并可回看响应 Body / Headers
- 设置页，支持主题、编辑器字体、文字大小、关于信息
- GitHub Release 更新检查，可配置 Owner / Repository
- 打开的请求 Tab 会实时保存，下次启动自动恢复

## 运行

项目沿用 Swell-SSH 的技术路线：`.NET + WinUI 3 + Windows App SDK`，目标框架为 `net10.0-windows10.0.19041.0`。

在 Windows 开发环境中执行：

```powershell
dotnet restore
dotnet run
```

gRPC 请求需要本机安装 `grpcurl`，并确保它在 `PATH` 中。请求体按 `grpcurl -d` 的 JSON 格式填写，方法填写为 `package.Service/Method`。

## 设置与更新

在左侧导航进入“设置”：

- 主题：跟随系统、浅色、深色
- 编辑器字体：应用到 Headers、Query、Body、环境变量、响应 Body / Headers
- 文字大小：控制编辑器与响应查看区域字号
- 更新：填写 GitHub Owner 和 Repository 后，会检查 `releases/latest`
- 关于：展示应用名称和当前主要功能

## 环境变量

在请求编辑区的“环境变量”页签中按行填写：

```text
baseUrl=https://api.example.com
token=dev-token
```

请求中可以这样使用：

```text
{{baseUrl}}/users
Authorization: Bearer {{token}}
```
