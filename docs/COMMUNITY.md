# 社区后端配置

社区 API 运行于 .NET 10，使用 ASP.NET Core Identity 处理密码散列与登录失败锁定，通过 MySQL 保存账号、会话、帖子和回复。桌面端使用 HTTPS 请求社区 API，本机游戏库使用原有的 SQLite。退出登录会立即关闭当前论坛窗口，联网时还会注销服务端会话。会话在桌面端保存在当前进程中，有效期为 12 小时。

## 服务端

创建一个专用、初始为空的 MySQL 数据库，使用 `utf8mb4`。首次启动会创建表，数据库账号需要该库的表结构创建权限。建表后，运行账号需要库内 `SELECT`、`INSERT`、`UPDATE`、`DELETE` 权限。用服务器环境变量 `ConnectionStrings__ForumDatabase` 提供连接字符串。应用和数据库同机时连接到 `127.0.0.1`。

在开发机运行后端时，可通过本机回环地址联调。以下命令假设已在当前环境配置数据库连接字符串。

```powershell
dotnet run --project src/GalgameManager.Community -- --urls http://127.0.0.1:5087
```

Linux 生产服务可以使用 `dotnet publish src/GalgameManager.Community -c Release -r linux-x64 --self-contained true` 构建。配置 Kestrel 的 HTTPS 地址和证书后，用 `GET /health` 检查进程和数据库初始化是否成功。部署进程可由 systemd 托管。

## 桌面端

在 `GalgameManager.exe` 同目录放置 `community.json`。

```json
{
  "apiBaseUrl": "https://forum.example.com/"
}
```

使用受信任 HTTPS 证书时，只需填写 API 地址。使用独立自签证书时，再在该文件设置 `certificateSha256`，值为不带分隔符的证书 SHA-256 指纹。客户端同时核验地址是否匹配证书。更换服务器证书时同步分发更新过的配置。

本地联调可设置 `GALGAMEMANAGER_COMMUNITY_URL=http://127.0.0.1:5087/`。打包时用 `scripts/Publish-Windows.ps1 -CommunitySettings <配置文件>` 把连接配置写入发布包。桌面配置只包含 API 地址和可选证书指纹。

## 接口

| 方法与路径 | 用途 |
| --- | --- |
| `POST /api/auth/register` | 用邮箱、昵称和密码注册。 |
| `POST /api/auth/login` | 登录并取得当前会话。 |
| `GET /api/auth/me` | 查看当前账号。 |
| `POST /api/auth/logout` | 注销当前会话。 |
| `GET /api/forum/topics` | 分页浏览并按标题搜索。 |
| `GET /api/forum/topics/{id}` | 读取主贴和分页回复。 |
| `POST /api/forum/topics` | 发帖。 |
| `POST /api/forum/topics/{id}/replies` | 回帖。 |

除注册和登录外，账号与论坛接口均要求有效会话。服务器根据会话识别发帖人与回复人。当前注册流程使用邮箱作为账号标识。邮箱验证和密码找回需接入邮件服务后启用。
