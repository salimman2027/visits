-- جداول الزيارات الميدانية (آمنة للتشغيل أكتر من مرة)
IF OBJECT_ID(N'dbo.Clients') IS NULL
CREATE TABLE dbo.Clients(
  Id nvarchar(100) NOT NULL PRIMARY KEY,
  Name nvarchar(200) NULL, Phone nvarchar(50) NULL, Sector nvarchar(100) NULL, Size nvarchar(50) NULL,
  Data nvarchar(max) NOT NULL CHECK (ISJSON(Data)=1),
  Updated datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME());

IF OBJECT_ID(N'dbo.Plans') IS NULL
CREATE TABLE dbo.Plans(
  Id nvarchar(100) NOT NULL PRIMARY KEY,
  Rep nvarchar(100) NULL, PlanDate date NULL,
  Data nvarchar(max) NOT NULL CHECK (ISJSON(Data)=1),
  Updated datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME());

IF OBJECT_ID(N'dbo.Visits') IS NULL
CREATE TABLE dbo.Visits(
  Id nvarchar(100) NOT NULL PRIMARY KEY,
  Rep nvarchar(100) NULL, VisitDate date NULL, ClientId nvarchar(100) NULL, Status nvarchar(30) NULL,
  Data nvarchar(max) NOT NULL CHECK (ISJSON(Data)=1),
  Updated datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME());

IF OBJECT_ID(N'dbo.Settings') IS NULL
CREATE TABLE dbo.Settings(
  Id nvarchar(100) NOT NULL PRIMARY KEY,
  Data nvarchar(max) NOT NULL CHECK (ISJSON(Data)=1),
  Updated datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME());

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Plans_Date_Rep')
  CREATE INDEX IX_Plans_Date_Rep ON dbo.Plans(PlanDate, Rep);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Visits_Date_Rep')
  CREATE INDEX IX_Visits_Date_Rep ON dbo.Visits(VisitDate, Rep);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Visits_Client')
  CREATE INDEX IX_Visits_Client ON dbo.Visits(ClientId);
