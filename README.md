# Sql2Fetch

**English** · A cross-platform desktop tool for querying Microsoft Dataverse (Dynamics 365 / Power Platform) with SQL.

- **SQL to FetchXML converter** — write a `SELECT` and get the FetchXML; works offline too
- **Run queries** against your environment and browse the results (sign in with browser OAuth / MFA, username + password, or device code)
- **Metadata browser** — table list with record counts and table types, column schema with option set values
- **ER diagram** for any table, exportable to PNG, Excel (`.xlsx`) or Mermaid
- Built with .NET 10 and [Avalonia](https://avaloniaui.net/) for macOS, Windows and Linux (so far only used on macOS). Self-contained builds are on the [Releases](https://github.com/graysonwang0911/Sql2Fetch/releases) page — no .NET install needed

The UI and the rest of this README are in Chinese.

---

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
- 表清单只在连接时加载一次；点清单上方的「刷新」才会重新读取环境里最新的表和记录数
- 表结构：点击表名显示所有列的逻辑名、显示名、类型和选项值，可搜索列名
- ER 图：以所选的表为中心，画出它引用的表（N:1）、引用它的表（1:N）和多对多的表（N:N）；点击图中的表可跳转过去
- ER 图上每张表都标注表类型（业务表 / 配置表 / 系统表 / 中间表，按元数据推断，仅供参考）
- ER 图导出：PNG 图片、Excel 表格（`.xlsx`，「表」和「关系」两张工作表），或 Mermaid 文本（存为 `.mmd` 文件 / 复制到剪贴板）；导出的是过滤后当前显示的内容

**登录方式**

- 浏览器登录（OAuth，支持 MFA）
- 用户名 + 密码（账号启用了 MFA 时不可用）
- 设备码

## 下载

在 [Releases](https://github.com/graysonwang0911/Sql2Fetch/releases) 页面下载对应平台的压缩包，解压即可运行，不需要安装 .NET：

| 平台 | 文件 | 启动 |
|---|---|---|
| Windows | `Sql2Fetch-<版本>-win-x64.zip`（ARM 设备用 `win-arm64`） | 双击 `Sql2Fetch.App.exe` |
| macOS | `osx-arm64`（Apple 芯片）或 `osx-x64`（Intel） | 终端里运行 `./Sql2Fetch.App` |
| Linux | `linux-x64` | 终端里运行 `./Sql2Fetch.App` |

运行包没有做代码签名：

- Windows 首次运行可能弹出 SmartScreen 提示，点「更多信息 → 仍要运行」。
- macOS 会拦截从网上下载的未签名程序，解压后先执行一次：
  ```bash
  xattr -dr com.apple.quarantine Sql2Fetch-osx-arm64
  ```

## 从源码运行

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
| `TOP`、`DISTINCT`、`OFFSET ... FETCH` | FetchXML 的 `top` 只接受 1–5000；`TOP` 超过 5000 时改为执行时翻页取够行数，`TOP 0` 只返回表头；`FETCH NEXT` 超过 5000 时同样改为执行时翻页 |
| `WHERE` | `AND` / `OR` / `NOT`、比较运算、`IN`、`BETWEEN`、`LIKE`、`IS NULL` |
| `JOIN` | `INNER`、`LEFT`、`RIGHT`（转成 `LEFT`），支持多层嵌套和额外的 `ON` 条件 |
| 聚合 | `COUNT`、`SUM`、`AVG`、`MIN`、`MAX`，`GROUP BY` |
| 按日期分组 | `YEAR(col)`、`DATEPART(month, col)` 等 |
| `HAVING` | FetchXML 本身不支持，由 Sql2Fetch 取回数据后在本地过滤；同时写了 `TOP` / `OFFSET` 时，也改为过滤之后在本地应用 |
| `ORDER BY` | 聚合查询里可以直接写 `ORDER BY COUNT(*) DESC` |
| FetchXML 运算符函数 | 如 `createdon = lastxdays(7)`、`ownerid = eq_userid()`、`thismonth()` |

转换不了的写法见下一节。

## 不能转换的 SQL 语法

FetchXML 的表达能力比 SQL 小得多：它只能描述“从一张主表出发，通过等值关联带上其他表，用列和常量做比较”的查询，没有子查询，也不能对列做运算。所以下面这些写法无法转换。遇到时不会生成 FetchXML，状态栏会说明原因，并在编辑器里选中出错的位置。

### 语句类型

| 写法 | 说明 |
|---|---|
| `INSERT` / `UPDATE` / `DELETE` / `MERGE`、建表等 | 本工具只读，只接受 `SELECT` |
| 一次写多条语句 | 一次只能翻译一条，`DECLARE` 加 `SELECT` 也算两条 |
| `WITH ... AS (...)`（CTE） | |
| `UNION` / `UNION ALL` / `INTERSECT` / `EXCEPT` | 分开查询 |
| 没有 `FROM` 的查询，如 `SELECT 1`、`SELECT GETDATE()` | |
| 变量，如 `WHERE statecode = @n`、`TOP (@n)` | 直接写常量 |

### FROM 和 JOIN

| 写法 | 说明 |
|---|---|
| `FULL OUTER JOIN` | FetchXML 只有内连接和左外连接（`RIGHT JOIN` 会自动改写成 `LEFT JOIN`） |
| `CROSS JOIN`、`CROSS APPLY` / `OUTER APPLY` | |
| 逗号连接多表：`FROM account a, contact c` | 改用 `JOIN ... ON` |
| 派生表：`FROM (SELECT ...) x` | |
| `PIVOT` / `UNPIVOT` | |
| `ON` 里没有“左表列 = 右表列”的等值条件，如 `ON c.createdon > a.createdon`、`ON 1 = 1` | 必须有一个等值条件作为关联键 |
| `ON` 里用 `OR`：`ON a.x = c.y OR c.z = 0` | |
| `ON` 的附加条件引用了左表，如 `... AND a.statecode = 0` | 附加条件只能用右表自己的列和常量；其余条件移到 `WHERE` |
| 多列联合关联：`ON a.x = c.y AND a.z = c.w` | 只能用一对列做关联；第二对列的条件移到 `WHERE` |
| 带架构或数据库名的表名，如 `sales.account`、`db.dbo.account` | 直接写表的逻辑名；`dbo.account` 可以 |
| `NOLOCK` 以外的表提示，如 `WITH (INDEX(...))` | |

### SELECT 列

`SELECT` 里只能写列本身、`*`、`表别名.*`、聚合函数，以及分组查询里的日期分组函数（`YEAR(col)`、`DATEPART(month, col)` 等，必须同时写在 `GROUP BY` 里）。下面这些都不行：

| 写法 | 例子 |
|---|---|
| 运算和拼接 | `revenue * 2`、`name + 'x'` |
| 常量列 | `SELECT 'x' AS c, name` |
| `CASE WHEN ...` | |
| 标量函数 | `UPPER(name)`、`ISNULL(name, '')`、`CAST(...)`、`CONVERT(...)` |
| 窗口函数 | `ROW_NUMBER() OVER (...)`、`COUNT(*) OVER ()` |
| 子查询列 | `SELECT name, (SELECT COUNT(*) FROM contact)` |
| 含空格或符号的别名 | `name AS [my name]`（别名只能是字母、数字、下划线；中文可以） |
| 三段以上的列名 | `dbo.account.name`、`a.b.c.name`（只能写 `列名` 或 `表别名.列名`） |

这些计算需要取回数据后自己处理。

### WHERE 条件

条件只能是“列 和 常量比较”或“列 和 列比较”。列和列比较只能用 `=`、`<>`、`>`、`>=`、`<`、`<=`；`IN`、`LIKE`、`BETWEEN` 的值只能是常量。

| 写法 | 例子 | 替代办法 |
|---|---|---|
| 对列做函数或运算后再比较 | `LEN(name) > 3`、`YEAR(createdon) = 2024`、`revenue * 2 > 100` | 日期范围用 `BETWEEN` 或下面的日期函数 |
| 比较的值是表达式或类型转换 | `revenue > 10 + 5`、`createdon >= CAST('2024-01-01' AS date)` | 先算好再写常量；日期直接写 `'2024-01-01'` |
| `IN` / `LIKE` / `BETWEEN` 的值是列 | `name IN (telephone1, 'x')`、`name LIKE telephone1`、`revenue BETWEEN numberofemployees AND 100` | |
| 两边都是常量的条件 | `WHERE 1 = 1 AND statecode = 0` | 去掉这个条件 |
| SQL 的日期函数 | `createdon > GETDATE()`、`DATEADD(day, -7, GETDATE())` | 用 FetchXML 的日期函数：`createdon = lastxdays(7)`、`today()`、`thismonth()` 等 |
| 子查询 | `IN (SELECT ...)`、`EXISTS (...)`、`revenue > (SELECT AVG(...))` | 改写成 `JOIN` |
| `= NULL` / `<> NULL` | | 用 `IS NULL` / `IS NOT NULL` |
| `LIKE ... ESCAPE` | | |
| 全文检索 | `CONTAINS(name, 'x')`、`FREETEXT(...)` | |
| FetchXML 日期函数配 `=` 以外的运算符 | `createdon > lastxdays(7)` | 只能写 `createdon = lastxdays(7)` |
| 对 FetchXML 日期函数取反 | `NOT createdon = lastxdays(7)` | |

### 聚合、GROUP BY 和 HAVING

| 写法 | 说明 |
|---|---|
| `COUNT` / `SUM` / `AVG` / `MIN` / `MAX` 以外的聚合函数，如 `STRING_AGG` | |
| 聚合函数的参数是表达式：`SUM(revenue * 2)` | 参数只能是列，或 `COUNT(*)` |
| 聚合查询里用 `*`：`SELECT *, COUNT(*) ...` | 写出具体的列 |
| `SELECT` 里的非聚合列没有出现在 `GROUP BY` 里 | 和标准 SQL 的要求一样 |
| `GROUP BY ROLLUP / CUBE / GROUPING SETS` | |
| 按表达式分组：`GROUP BY UPPER(name)` | 只能按列，或按日期的一部分分组 |
| `DATEPART` 的 hour、minute 等 | 只支持 year、quarter、month、week、day、fiscalperiod、fiscalyear |
| `HAVING` 里的 `LIKE`、子查询、运算，如 `HAVING COUNT(*) * 2 > 4` | `HAVING` 只能拿聚合函数、分组列或 `SELECT` 别名和常量比较；比较运算、`IN`、`BETWEEN`、`IS NULL` 和 `AND` / `OR` / `NOT` 都可以用 |
| 没有聚合也没有 `GROUP BY` 却写了 `HAVING` | |

### ORDER BY、TOP 和分页

| 写法 | 说明 |
|---|---|
| 按序号排序：`ORDER BY 1` | 写列名 |
| 按表达式排序：`ORDER BY LEN(name)`、`ORDER BY revenue * 2` | |
| 聚合查询里按未分组的列排序 | 只能按分组列、聚合函数或 `SELECT` 别名排序 |
| `TOP ... PERCENT`、`TOP ... WITH TIES` | |
| `TOP` 和 `OFFSET ... FETCH` 同时使用 | 分页只用 `OFFSET ... FETCH` |
| 只有 `OFFSET` 没有 `FETCH NEXT` | |
| `OFFSET` 不是 `FETCH NEXT` 行数的整数倍，如 `OFFSET 7 ROWS FETCH NEXT 5 ROWS` | FetchXML 只能整页取，例如 `OFFSET 5`、`OFFSET 10` |
| `FETCH NEXT 0 ROWS` | |

### 不报错但会被忽略的写法

下面这些子句不会报错，但对生成的 FetchXML **没有任何作用**，容易让人误以为生效了：

- `SELECT ... INTO #temp`：不会创建任何表，只是普通查询
- `FOR JSON` / `FOR XML`
- `OPTION (...)` 查询提示
- `TABLESAMPLE (...)`
- `COLLATE ...`

还有两种写法不会报错，但意思和你想的不一样：

- **双引号不是字符串**：`WHERE name = "x"` 里的 `"x"` 按 SQL 规则是列名，会被翻译成“和名为 x 的列比较”。字符串要用单引号 `'x'`。离线模式下只给出警告；连接后因为会校验列名，通常会直接报“不存在列”。
- **`TRUE` / `FALSE` 不是常量**：`WHERE statecode = TRUE` 里的 `TRUE` 同样被当成列名。布尔值请写 `1` / `0`。

另外有两点行为和 SQL Server 不完全一样：

- **`LIKE` 的通配符**原样交给 Dataverse 处理。`%` 和 `_` 可以放心用，`[abc]` 这类字符集写法能否生效取决于 Dataverse，本工具不做转换。
- **跨表的列比较**（如 `WHERE a.revenue > c.statecode`）可以转换，但需要较新版本的 Dataverse 才能执行，翻译时会给出警告。

## 几点说明

- **记录数不是实时的**：表清单里的记录数来自 Dataverse 定期刷新的统计值，可能滞后最多约一天。要精确数字请执行 `SELECT COUNT(*)`。
- **表类型是推断的**：「业务 / 配置 / 系统 / 中间表」是根据所有权类型、是否自定义表等元数据推断出来的，不是 Dataverse 的官方分类，仅供参考。鼠标悬停在类型上可以看到判断依据。
- **翻页上限**：一次执行默认最多取 10 页，超过时状态栏会提示还有更多数据。
- **取消**：连接或执行查询的过程中，点工具栏的「取消」可以中止。

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
