# MajDHTProvider

MajDHTProvider 是一个 .NET 10 命令行程序，通过 [MajDHTNetwork](modules/MajDHTNetwork)
分发 MajdataPlay 在线谱面的资源文件。它有两种模式：

- **Publisher**：扫描谱面目录，把每个符合布局的资源文件以发布者身份签名发布；文件或谱面
  目录被删除时发布撤销。
- **Replica**：为受信任的发布者提供资源。Peer 请求某个资源时，若本地已有且与发布内容一致则
  直接返回；否则从源站（Publisher）回源下载、保存到本地并转发给请求方。本地文件仅在发布者
  撤销时删除。

两种模式都会监控目录变动。

## 目录布局与密钥

两种模式使用相同的存储布局，只处理以下文件（文件名区分大小写，谱面目录名必须是带连字符
的 GUID，大小写均可）：

| 文件 | 密钥名 |
| --- | --- |
| `<GUID>/maidata.txt` | `majnet://chart-resources/by-uuid/<GUID>/maidata` |
| `<GUID>/track.mp3` | `majnet://chart-resources/by-uuid/<GUID>/track` |
| `<GUID>/bg.jpg` | `majnet://chart-resources/by-uuid/<GUID>/image` |
| `<GUID>/bg_fullSize.jpg` | `majnet://chart-resources/by-uuid/<GUID>/image-fullsize` |
| `<GUID>/bg.mp4` | `majnet://chart-resources/by-uuid/<GUID>/video` |

密钥中的 `<GUID>` 始终是小写的 `D` 格式（与 MajdataPlay 一致）。完整的 DHT 密钥是
`<发布者指纹>:<密钥名>`；客户端用 `new DHTKey(publisherId, keyName)` 构造。其他文件、
子目录以及非 GUID 目录都会被忽略。

## 构建与运行

```powershell
git submodule update --init
dotnet build MajDHTProvider.slnx
dotnet run --project src/MajDHTProvider -- publisher --root D:\charts
dotnet run --project src/MajDHTProvider -- replica --root D:\mirror --publisher <发布者指纹> --bootstrap 203.0.113.10:42000
```

Publisher 启动时会打印 `Publisher ID for replicas and clients: <指纹>`，Replica 的 `--publisher`
以及客户端构造密钥都使用这个值。`--help` 列出全部选项，常用选项如下：

| 选项 | 说明 |
| --- | --- |
| `--root <dir>` | 谱面根目录（必填）。 |
| `--data <dir>` | 身份种子、发布账本与暂存目录，默认 `<root>/.majdht`。 |
| `--listen` / `--advertise` / `--bootstrap` | 监听地址、对外通告地址、引导节点 `ip:port`。 |
| `--tcp-port` / `--udp-port` | 端口，默认 42000，`none` 关闭该传输。 |
| `--relay` | 同时为其他节点提供中继与 NAT 穿透协助。 |
| `--work-bits` | 网络要求的身份工作量，默认 12，全网必须一致。 |
| `--record-lifetime` | （Publisher）发布有效期，默认 24h，到半衰期自动续签。 |
| `--refresh-interval` | （Replica）检查撤销与更新的间隔，默认 10m。 |
| `--pull-wait` | （Replica）Peer 请求等待回源的时长，默认 3s，应小于 5 秒的请求超时。 |
| `--quiet-period` / `--rescan-interval` | 目录事件静默期（默认 2s）与全量重扫间隔（默认 10m）。 |

`<data>/identity.key` 是节点身份种子（机密），请备份并妥善保管；Publisher 丢失它将无法再
以同一发布者身份发布或撤销。

## 设计要点

- **文件即存储**：`FileBlockStore` 按 MajDHT v1 的叶子规则 `SHA256(0x00 || block)` 为磁盘
  文件建立块索引，直接从文件读取并重新校验数据块，不把整个谱面库复制进内存；尚未落盘的块
  （例如正在移动到位的下载）进入有界内存缓存。
- **Publisher**：所有变更串行处理。内容未变化（长度、修改时间或哈希一致）时不重复发布；内容
  变化时以更高修订号重新发布；文件或目录删除时签名撤销。`publications.json` 账本持久化每个
  密钥的最新修订号，修订号取 `max(上次 + 1, 当前 Unix 毫秒)`，因此重启后仍单调递增，离线
  期间被删除的资源会在下次启动时撤销。发布到达半衰期时用 `PublishAliasAsync` 续签，无需重读文件。
- **Replica**：通过 MajDHTNetwork 的 `IDHTContentFallback` 扩展点接管"本地未注册内容的块请求"：
  只接受受信任发布者、且密钥名属于谱面命名空间的请求；同一资源的并发请求合并为一次回源；先
  尝试用 `ImportContentAsync` 提供与发布内容一致的本地文件，否则下载到 `<data>/staging`、
  校验后原子移动到 `<GUID>/<文件>` 再提供。请求最多等待 `--pull-wait`，回源在后台继续，
  Peer 在此期间切换到其他提供者。
- **撤销与更新**：Replica 每个刷新周期解析已提供的资源：撤销则删除本地文件（目录为空时一并
  删除）；续签则跟随新修订号；内容更新则回源替换。手动删除或改动的文件只会停止提供，不视为
  撤销。启动时，与发布内容不一致的本地文件会被回源替换，暂时无法解析的文件保留但不提供。
- **目录监控**：`FileSystemWatcher` 事件按谱面合并，静默 `--quiet-period` 后再同步，避免处理
  复制到一半的文件；监控缓冲溢出或每个 `--rescan-interval` 触发全量重扫。

## MajDHTNetwork 扩展

为实现 Replica，子模块新增了（详见其 README）：

- `IDHTContentFallback` 与 `MajDHTNode` 构造参数 `contentFallback`：本地未注册内容被请求时按需取得。
- `MajDHTNode.ImportContentAsync(record, stream)`：用已有签名记录校验并提供本地副本，无需重新下载。
- `DHTNodeOptions.MaxLocalLeaves`：可配置的本地 Merkle 叶子配额（原为固定的 131,072，约 2 GiB）；
  Provider 默认 4,194,304（16 KiB 块时约 64 GiB）。

## 测试

```powershell
dotnet test src/MajDHTProvider.Tests/MajDHTProvider.Tests.csproj
dotnet test modules/MajDHTNetwork/src/MajDHTNetwork.Tests/MajDHTNetwork.Tests.csproj --configuration Release
```

Provider 测试覆盖布局与密钥映射、命令行解析、文件块存储、账本与身份持久化、目录监控、
Publisher/Replica 与真实回环 DHT 节点的集成，以及 Publisher 与 Replica 两个宿主的端到端流程。

## 限制

- 引导节点与通告地址只接受 IP，不解析主机名。
- Replica 只在刷新周期内发现撤销（默认 10 分钟）。
- 测试仅在 Windows 回环网络上运行，未在 Linux、真实 NAT/公网环境或 MajdataPlay 客户端中验证。
