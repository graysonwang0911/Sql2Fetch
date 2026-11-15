# Sql2Fetch

用 SQL 查询 Microsoft Dataverse（Dynamics 365 / Power Platform）的桌面工具。

写一条 `SELECT`，Sql2Fetch 把它翻译成 FetchXML，并可以直接在你的环境上执行、查看结果。同时它也是一个轻量的 Dataverse 元数据浏览器：表清单、表结构、ER 图都能看。

基于 .NET 10 和 [Avalonia](https://avaloniaui.net/) 开发，可在 macOS、Windows、Linux 上运行（目前只在 macOS 上实际使用过）。

## 功能

**SQL → FetchXML**

- 左边写 SQL，右边实时得到 FetchXML，可一键复制
- 不连接环境也能翻译（离线模式，不校验表名列名）
- 连接后会校验表名和列名，写错时给出位置和修改建议
- 自动补全：表名、列名、关键字、选项集的取值

**执行查询**

- 连接后按 F5 直接执行，结果显示在下方表格
- 自动翻页，默认显示格式化后的值（选项集标签、日期等）
- 查找（lookup）列会带上关联的表名，例如 `Contoso (account)`

**浏览元数据**

- 表清单：显示表名、类型、记录数，可搜索、按类型筛选、点表头排序
- 表结构：点击表名显示所有列的逻辑名、显示名、类型和选项值，可搜索列名
- ER 图：以所选的表为中心，画出它引用的表（N:1）、引用它的表（1:N）和多对多的表（N:N）；点击图中的表可跳转过去

**登录方式**

- 浏览器登录（OAuth，支持 MFA）
- 用户名 + 密码（账号启用了 MFA 时不可用）
- 设备码

## 运行

需要先安装 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```bash
git clone https://github.com/graysonwang0911/Sql2Fetch.git
cd Sql2Fetch
dotnet run --project src/Sql2Fetch.App
```

第一次运行会自动下载依赖并编译，需要一点时间。

## 使用

1. 在顶部「环境」里填 Dataverse 环境地址，例如 `https://org12345.crm7.dynamics.com/`
2. 选择登录方式，点「连接」。登录成功后令牌会缓存，下次通常不用再登录
3. 在 SQL 编辑器里写查询：
   - `Cmd+Enter` / `Ctrl+Enter`：翻译成 FetchXML
   - `F5`：翻译并执行
   - `Ctrl+Space`：手动弹出自动补全
4. 单击左侧的表：在下方「表结构」「ER 图」标签页查看它的信息
5. 双击左侧的表：把表名插入到 SQL 光标处

不连接也可以只用翻译功能。

## 支持的 SQL

只支持 `SELECT`。示例：

```sql
SELECT TOP 10 c.firstname, c.lastname, a.name AS AccountName
FROM contact c
JOIN account a ON c.parentcustomerid = a.accountid
WHERE a.statecode = 0 AND c.createdon = lastxdays(7)
ORDER BY c.createdon DESC
```

| 语法 | 说明 |
|---|---|
| `SELECT` 列、`*`、列别名 | |
| `TOP`、`DISTINCT`、`OFFSET ... FETCH` | |
| `WHERE` | `AND` / `OR` / `NOT`、比较运算、`IN`、`BETWEEN`、`LIKE`、`IS NULL` |
| `JOIN` | `INNER`、`LEFT`、`RIGHT`（转成 `LEFT`），支持多层嵌套和额外的 `ON` 条件 |
| 聚合 | `COUNT`、`SUM`、`AVG`、`MIN`、`MAX`，`GROUP BY` |
| 按日期分组 | `YEAR(col)`、`DATEPART(month, col)` 等 |
| `HAVING` | FetchXML 本身不支持，由 Sql2Fetch 取回数据后在本地过滤 |
| `ORDER BY` | |
| FetchXML 运算符函数 | 如 `createdon = lastxdays(7)`、`ownerid = eq_userid()`、`thismonth()` |

**不支持：**

- `INSERT` / `UPDATE` / `DELETE`（本工具只读）
- 子查询、`UNION`、`FULL OUTER JOIN`
- 列上的函数和运算，例如 `LEN(name) > 3`、`name + 'x'`

遇到不支持的写法时，状态栏会说明原因和出错位置。

## 几点说明

- **记录数不是实时的**：表清单里的记录数来自 Dataverse 定期刷新的统计值，可能滞后最多约一天。要精确数字请执行 `SELECT COUNT(*)`。
- **表类型是推断的**：「业务 / 配置 / 系统 / 中间表」是根据所有权类型、是否自定义表等元数据推断出来的，不是 Dataverse 的官方分类，仅供参考。鼠标悬停在类型上可以看到判断依据。
- **翻页上限**：一次执行默认最多取 10 页，超过时状态栏会提示还有更多数据。

## 本地数据

Sql2Fetch 在用户目录下的 `.sql2fetch` 文件夹里保存两样东西：

- `settings.json`：环境地址、上次的 SQL、登录方式、用户名、表清单的排序和筛选
- `msal_cache.dat`：登录令牌缓存，由系统加密保护（macOS 钥匙串 / Windows DPAPI / Linux libsecret）

密码不会保存到磁盘。在应用里点「断开」会清除已缓存的登录。

## 项目结构

```
src/
  Sql2Fetch.Engine      SQL 解析和 FetchXML 生成、自动补全（不依赖网络）
  Sql2Fetch.Dataverse   登录、Web API 调用、元数据读取、FetchXML 执行
  Sql2Fetch.App         Avalonia 桌面界面
tests/
  Sql2Fetch.Engine.Tests   引擎的单元测试
```

SQL 解析使用微软的 `Microsoft.SqlServer.TransactSql.ScriptDom`，登录使用 MSAL。

运行测试：

```bash
dotnet test
```
