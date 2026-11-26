# الزيارات الميدانية — ASP.NET Core + SQL Server

الواجهة (`wwwroot/index.html`) بتتكلم مع API صغير (`Program.cs`) بيخزّن في SQL Server.
جداول: `Clients` · `Plans` · `Visits` · `Settings` (أعمدة مفهرسة + عمود `Data` JSON فيه الحقل كامل).
المناديب بيتزامنوا تلقائي (الواجهة بتتحقق من التغييرات كل ١٥ ثانية).

## تشغيل محلي
1. عدّل `ConnectionStrings:Default` في `appsettings.json` (أو LocalDB/SQL Express).
2. `dotnet run` ثم افتح الرابط اللي هيظهر. الجداول بتتعمل لوحدها أول تشغيل.

## MonsterASP.NET
1. من اللوحة: أنشئ قاعدة **MSSQL** وخد الـ Connection String (Server / Database / User / Password).
2. حط في `appsettings.Production.json` على السيرفر (مش بيترفع على GitHub):
   ```json
   { "ConnectionStrings": { "Default": "Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True" },
     "AccessCode": "كود-سري-للمناديب" }
   ```
3. ارفع ناتج `dotnet publish -c Release -o publish` على الموقع (Web Deploy من Visual Studio أو FTP).
4. تأكد إن نسخة .NET في `FieldVisits.csproj` متدعومة في خطة الاستضافة بتاعتك.

## GitHub
```bash
git init && git add . && git commit -m "first commit"
git branch -M main
git remote add origin https://github.com/<user>/<repo>.git
git push -u origin main
```
للنشر التلقائي عند كل push: أضف في Settings → Secrets → Actions:
`DB_CONNECTION` · `ACCESS_CODE` · `FTP_SERVER` · `FTP_USER` · `FTP_PASSWORD`
(راجع `.github/workflows/deploy.yml`، ومسار `server-dir` لازم يطابق مجلد موقعك على FTP).

## ملاحظات أمان
- **حدد `AccessCode`**: من غيره أي حد معاه الرابط يقدر يقرأ ويعدّل البيانات.
- ما ترفعش الـ Connection String في GitHub؛ `appsettings.Production.json` متعمله ignore.
- فعّل HTTPS من لوحة الاستضافة.
