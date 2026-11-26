using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var cs = builder.Configuration.GetConnectionString("Default")
         ?? throw new InvalidOperationException("ConnectionStrings:Default is missing");
var accessCode = builder.Configuration["AccessCode"] ?? "";

// إنشاء الجداول أول تشغيل (لو فشل الموقع يفضل شغال والـ log يوضح السبب)
try
{
    await using var c = new SqlConnection(cs);
    await c.OpenAsync();
    var sql = await File.ReadAllTextAsync(Path.Combine(app.Environment.ContentRootPath, "db", "schema.sql"));
    await using var cmd = new SqlCommand(sql, c);
    await cmd.ExecuteNonQueryAsync();
}
catch (Exception ex) { app.Logger.LogError(ex, "Database init failed"); }

// ping مفتوح: الواجهة تعرف منه إن الـ API موجود وهل محتاج كود دخول
app.MapGet("/api/ping", () => Results.Json(new { ok = true, auth = accessCode.Length > 0 }));

// باقي الـ API محمي بكود دخول (لو متحدد في الإعدادات)
app.Use(async (ctx, next) =>
{
    if (accessCode.Length > 0 && ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Path != "/api/ping")
    {
        var given = Encoding.UTF8.GetBytes(ctx.Request.Headers["X-Access-Code"].ToString());
        var want = Encoding.UTF8.GetBytes(accessCode);
        if (given.Length != want.Length || !CryptographicOperations.FixedTimeEquals(given, want))
        { ctx.Response.StatusCode = 401; return; }
    }
    await next();
});

// أعمدة مفهرسة لكل جدول: (اسم العمود، اسم الحقل في JSON، النوع، الطول)
var map = new Dictionary<string, (string Table, (string Col, string Json, bool IsDate, int Len)[] Cols)>
{
    ["clients"]  = ("Clients",  new[] { ("Name","name",false,200), ("Phone","phone",false,50), ("Sector","sector",false,100), ("Size","size",false,50) }),
    ["plans"]    = ("Plans",    new[] { ("Rep","rep",false,100), ("PlanDate","date",true,0) }),
    ["visits"]   = ("Visits",   new[] { ("Rep","rep",false,100), ("VisitDate","date",true,0), ("ClientId","cid",false,100), ("Status","status",false,30) }),
    ["settings"] = ("Settings", Array.Empty<(string, string, bool, int)>()),
};

static string? Str(JsonElement e, string name, int len) =>
    e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        ? (v.GetString() is { } s ? (s.Length > len ? s[..len] : s) : null) : null;

static DateTime? Dt(JsonElement e, string name) =>
    e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
    && DateTime.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

static async Task<List<JsonElement>> ReadAll(SqlConnection c, string table)
{
    var list = new List<JsonElement>();
    await using var cmd = new SqlCommand($"SELECT Data FROM dbo.{table}", c);
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync()) { using var doc = JsonDocument.Parse(r.GetString(0)); list.Add(doc.RootElement.Clone()); }
    return list;
}

// كل البيانات في طلب واحد
app.MapGet("/api/data", async () =>
{
    await using var c = new SqlConnection(cs);
    await c.OpenAsync();
    var settings = (await ReadAll(c, "Settings")).Cast<JsonElement?>().FirstOrDefault();
    return Results.Json(new
    {
        clients = await ReadAll(c, "Clients"),
        plans = await ReadAll(c, "Plans"),
        visits = await ReadAll(c, "Visits"),
        settings
    });
});

// بصمة خفيفة: الواجهة بتسأل عليها كل ١٥ ثانية ولو اتغيرت تسحب البيانات
app.MapGet("/api/stamp", async () =>
{
    await using var c = new SqlConnection(cs);
    await c.OpenAsync();
    var parts = new List<string>();
    foreach (var t in new[] { "Clients", "Plans", "Visits", "Settings" })
    {
        await using var cmd = new SqlCommand($"SELECT CONCAT(COUNT(*),'/',CONVERT(varchar(30),MAX(Updated),126)) FROM dbo.{t}", c);
        parts.Add((await cmd.ExecuteScalarAsync())?.ToString() ?? "");
    }
    return Results.Json(new { stamp = string.Join("|", parts) });
});

// حفظ (إضافة أو تعديل)
app.MapPut("/api/{col}/{id}", async (string col, string id, JsonElement body) =>
{
    if (!map.TryGetValue(col, out var m) || id.Length is 0 or > 100) return Results.BadRequest();
    var names = m.Cols.Select(x => x.Col).ToArray();
    var sql = $"UPDATE dbo.{m.Table} SET " + string.Concat(names.Select(n => $"{n}=@{n},")) +
              $"Data=@Data, Updated=SYSUTCDATETIME() WHERE Id=@Id; " +
              $"IF @@ROWCOUNT=0 INSERT dbo.{m.Table}(Id," + string.Concat(names.Select(n => n + ",")) +
              "Data,Updated) VALUES(@Id," + string.Concat(names.Select(n => "@" + n + ",")) + "@Data,SYSUTCDATETIME());";
    await using var c = new SqlConnection(cs);
    await c.OpenAsync();
    await using var cmd = new SqlCommand(sql, c);
    cmd.Parameters.AddWithValue("@Id", id);
    cmd.Parameters.AddWithValue("@Data", body.GetRawText());
    foreach (var x in m.Cols)
        cmd.Parameters.AddWithValue("@" + x.Col, x.IsDate ? (object?)Dt(body, x.Json) ?? DBNull.Value
                                                          : (object?)Str(body, x.Json, x.Len) ?? DBNull.Value);
    await cmd.ExecuteNonQueryAsync();
    return Results.NoContent();
});

// حذف
app.MapDelete("/api/{col}/{id}", async (string col, string id) =>
{
    if (!map.TryGetValue(col, out var m)) return Results.BadRequest();
    await using var c = new SqlConnection(cs);
    await c.OpenAsync();
    await using var cmd = new SqlCommand($"DELETE FROM dbo.{m.Table} WHERE Id=@Id", c);
    cmd.Parameters.AddWithValue("@Id", id);
    await cmd.ExecuteNonQueryAsync();
    return Results.NoContent();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.Run();
