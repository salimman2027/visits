using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// أي خطأ غير متوقع: سجّله وارجع رسالة بدل ما الاتصال يتقفل
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled error on {Path}", ctx.Request.Path);
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsync("Server error: " + ex.GetType().Name + ": " + (ex.Message.Length > 200 ? ex.Message[..200] : ex.Message));
        }
    }
});

var cs = builder.Configuration.GetConnectionString("Default")
         ?? throw new InvalidOperationException("ConnectionStrings:Default is missing");
var accessCode = (builder.Configuration["AccessCode"] ?? "").Trim();

string? initError = null;
// إنشاء الجداول أول تشغيل (لو فشل الموقع يفضل شغال والـ log يوضح السبب)
try
{
    await using var c = new SqlConnection(cs);
    await c.OpenAsync();
    var sql = await File.ReadAllTextAsync(Path.Combine(app.Environment.ContentRootPath, "db", "schema.sql"));
    await using var cmd = new SqlCommand(sql, c);
    await cmd.ExecuteNonQueryAsync();
    // admin / admin افتراضي. بيتغير لأول مرة من زر "كلمة السر" في الصفحة، وبعدها ما بيتلمسش
    await using var adm = new SqlCommand(@"IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Id='admin')
          INSERT dbo.Users(Id,Username,Hash,Role,Active,Changed) VALUES('admin','admin',@h,'admin',1,0)
        ELSE UPDATE dbo.Users SET Hash=@h, Active=1 WHERE Id='admin' AND Changed=0", c);
    adm.Parameters.AddWithValue("@h", HashPw("admin"));
    await adm.ExecuteNonQueryAsync();
}
catch (Exception ex) { initError = ex.GetType().Name + ": " + ex.Message; app.Logger.LogError(ex, "Database init failed"); }

// ===== حسابات وصلاحيات (admin / rep) =====
var key = SHA256.HashData(Encoding.UTF8.GetBytes(cs + accessCode + "fv-token"));
string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
string Un64(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Encoding.UTF8.GetString(Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='))); }
string Sign(string s) => Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(s))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
var fails = new System.Collections.Concurrent.ConcurrentDictionary<string, (int n, DateTime t)>();

app.MapGet("/api/ping", () => Results.Json(new { ok = true, auth = true, db = initError }));

app.MapPost("/api/login", async (JsonElement b) =>
{
    var u = (Str(b, "u", 100) ?? "").Trim().ToLowerInvariant(); var pw = Str(b, "p", 200) ?? "";
    if (fails.TryGetValue(u, out var f) && f.n >= 8 && DateTime.UtcNow - f.t < TimeSpan.FromMinutes(10)) return Results.StatusCode(429);
    await using var c = new SqlConnection(cs); await c.OpenAsync();
    await using var cmd = new SqlCommand("SELECT Id,Hash,Role,RepId FROM dbo.Users WHERE Username=@u AND Active=1", c);
    cmd.Parameters.AddWithValue("@u", u);
    await using var r = await cmd.ExecuteReaderAsync();
    if (await r.ReadAsync() && CheckPw(pw, r.GetString(1)))
    {
        fails.TryRemove(u, out _);
        var pl = r.GetString(0) + "|" + DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        return Results.Json(new { token = B64(pl) + "." + Sign(pl), role = r.GetString(2), repId = r.IsDBNull(3) ? "" : r.GetString(3) });
    }
    fails.AddOrUpdate(u, (1, DateTime.UtcNow), (_, o) => (o.n + 1, DateTime.UtcNow));
    await Task.Delay(500);
    return Results.Unauthorized();
});

app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (!path.StartsWithSegments("/api") || path == "/api/ping" || path == "/api/login") { await next(); return; }
    try
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        var parts = (h.StartsWith("Bearer ") ? h[7..] : "").Split('.');
        if (parts.Length == 2)
        {
            var pl = Un64(parts[0]); var f = pl.Split('|');
            if (f.Length == 2 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(pl)), Encoding.UTF8.GetBytes(parts[1]))
                && long.Parse(f[1]) > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                await using var c = new SqlConnection(cs); await c.OpenAsync();
                await using var cmd = new SqlCommand("SELECT Role,RepId FROM dbo.Users WHERE Id=@i AND Active=1", c);
                cmd.Parameters.AddWithValue("@i", f[0]);
                await using var r = await cmd.ExecuteReaderAsync();
                if (await r.ReadAsync()) { ctx.Items["uid"] = f[0]; ctx.Items["role"] = r.GetString(0); ctx.Items["rep"] = r.IsDBNull(1) ? "" : r.GetString(1); await next(); return; }
            }
        }
    }
    catch (FormatException) { }
    catch (OverflowException) { }
    ctx.Response.StatusCode = 401;
});

static string HashPw(string p) { var salt = RandomNumberGenerator.GetBytes(16); return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(p, salt, 100000, HashAlgorithmName.SHA256, 32)); }
static bool CheckPw(string p, string s) { var a = s.Split(':'); return a.Length == 2 && CryptographicOperations.FixedTimeEquals(Rfc2898DeriveBytes.Pbkdf2(p, Convert.FromBase64String(a[0]), 100000, HashAlgorithmName.SHA256, 32), Convert.FromBase64String(a[1])); }
static bool IsRep(HttpContext c) => (string?)c.Items["role"] == "rep";

app.MapGet("/api/users", async (HttpContext ctx) =>
{
    if (IsRep(ctx)) return Results.Forbid();
    await using var c = new SqlConnection(cs); await c.OpenAsync();
    await using var cmd = new SqlCommand("SELECT RepId,Username,Active FROM dbo.Users WHERE Role='rep'", c);
    await using var r = await cmd.ExecuteReaderAsync();
    var l = new List<object>();
    while (await r.ReadAsync()) l.Add(new { repId = r.GetString(0), username = r.GetString(1), active = r.GetBoolean(2) });
    return Results.Json(l);
});

app.MapPut("/api/users/{repId}", async (HttpContext ctx, string repId, JsonElement b) =>
{
    if (IsRep(ctx)) return Results.Forbid();
    var u = (Str(b, "username", 100) ?? "").Trim().ToLowerInvariant(); var pw = Str(b, "password", 200);
    if (u.Length < 3 || (pw != null && pw.Length is > 0 and < 6)) return Results.BadRequest();
    var act = !(b.TryGetProperty("active", out var av) && av.ValueKind == JsonValueKind.False);
    try
    {
        await using var c = new SqlConnection(cs); await c.OpenAsync();
        await using var cmd = new SqlCommand(@"UPDATE dbo.Users SET Username=@u, Active=@a, Hash=ISNULL(@h,Hash) WHERE Id=@i AND Role='rep';
            IF @@ROWCOUNT=0 AND @h IS NOT NULL INSERT dbo.Users(Id,Username,Hash,Role,RepId,Active) VALUES(@i,@u,@h,'rep',@i,@a);", c);
        cmd.Parameters.AddWithValue("@u", u); cmd.Parameters.AddWithValue("@a", act); cmd.Parameters.AddWithValue("@i", repId);
        cmd.Parameters.AddWithValue("@h", string.IsNullOrEmpty(pw) ? DBNull.Value : HashPw(pw));
        await cmd.ExecuteNonQueryAsync();
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627) { return Results.Conflict(); }
    return Results.NoContent();
});

app.MapPost("/api/password", async (HttpContext ctx, JsonElement b) =>
{
    var old = Str(b, "old", 200) ?? ""; var nw = Str(b, "new", 200) ?? "";
    if (nw.Length < 6) return Results.BadRequest();
    await using var c = new SqlConnection(cs); await c.OpenAsync();
    await using var q = new SqlCommand("SELECT Hash FROM dbo.Users WHERE Id=@i", c);
    q.Parameters.AddWithValue("@i", (string?)ctx.Items["uid"] ?? "");
    var h = (string?)await q.ExecuteScalarAsync();
    if (h == null || !CheckPw(old, h)) return Results.Unauthorized();
    await using var u = new SqlCommand("UPDATE dbo.Users SET Hash=@h, Changed=1 WHERE Id=@i", c);
    u.Parameters.AddWithValue("@h", HashPw(nw)); u.Parameters.AddWithValue("@i", (string?)ctx.Items["uid"] ?? "");
    await u.ExecuteNonQueryAsync();
    return Results.NoContent();
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

// بنجمع الـ JSON مباشرة من عمود Data. المندوب بياخد خطته وزياراته بس، والإعدادات من غير الإيميل والـ webhook
static async Task<string> ReadJson(SqlConnection c, string sql, string? rep = null)
{
    var sb = new StringBuilder("[");
    await using var cmd = new SqlCommand(sql, c);
    if (rep != null) cmd.Parameters.AddWithValue("@r", rep);
    await using var r = await cmd.ExecuteReaderAsync();
    var first = true;
    while (await r.ReadAsync()) { if (!first) sb.Append(','); first = false; sb.Append(r.GetString(0)); }
    return sb.Append(']').ToString();
}

app.MapGet("/api/data", async (HttpContext ctx) =>
{
    var rep = IsRep(ctx) ? (string?)ctx.Items["rep"] ?? "" : null;
    var w = rep == null ? "" : " WHERE Rep=@r";
    await using var c = new SqlConnection(cs);
    await c.OpenAsync();
    var clients = await ReadJson(c, "SELECT Data FROM dbo.Clients");
    var plans = await ReadJson(c, "SELECT Data FROM dbo.Plans" + w, rep);
    var visits = await ReadJson(c, "SELECT Data FROM dbo.Visits" + w, rep);
    var st = await ReadJson(c, rep == null ? "SELECT Data FROM dbo.Settings" : "SELECT JSON_MODIFY(JSON_MODIFY(Data,'$.hook',''),'$.to','') FROM dbo.Settings");
    var settings = st.Length > 2 ? st[1..^1] : "null";
    return Results.Content("{\"clients\":" + clients + ",\"plans\":" + plans + ",\"visits\":" + visits + ",\"settings\":" + settings + "}", "application/json; charset=utf-8");
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
app.MapPut("/api/{col}/{id}", async (HttpContext ctx, string col, string id, JsonElement body) =>
{
    if (!map.TryGetValue(col, out var m) || id.Length is 0 or > 100) return Results.BadRequest();
    if (IsRep(ctx) && (col == "settings" || (col is "plans" or "visits" && Str(body, "rep", 100) != (string?)ctx.Items["rep"]))) return Results.Forbid();
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
app.MapDelete("/api/{col}/{id}", async (HttpContext ctx, string col, string id) =>
{
    if (IsRep(ctx) || !map.TryGetValue(col, out var m)) return Results.BadRequest();
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
