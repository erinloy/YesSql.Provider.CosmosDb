delete from [Document] where [Id] = @p;
delete from [PersonByName] where [DocumentId] = @p;
delete from [RecordIndexingTask] where [Category] = @p and [RecordId] IN (@p0, @p1, @p2);
delete from [tpArticlesByDay] where [Id] = @p;
delete from [tpArticlesByDay_Document] where [ArticlesByDayId] = @p;
delete from [tpArticlesByDay_Document] where [DocumentId] = @p and [ArticlesByDayId] = @p;
delete from [tpCol1_Document] where [Id] = @p;
delete from [tpDocument] where [Id] = @p;
delete from [tpPersonByAge] where [DocumentId] = @p;
delete from [tpPersonByName] where [DocumentId] = @p;
delete from [tpPersonIdentity] where [DocumentId] = @p;
delete from [tpUserByRoleNameIndex] where [Id] = @p;
delete from [tpUserByRoleNameIndex_Document] where [UserByRoleNameIndexId] = @p;
insert into [ArticlesByDay] ([Day], [Count]) values (@p, @p) RETURNING [Id];
insert into [ArticlesByDay_Document] ([ArticlesByDayId], [DocumentId]) values (@p, @p);
insert into [Document] ([Id], [Type], [Content], [Version]) values (@p, @p, @p, @p);
insert into [OpenId_Document] ([Id], [Type], [Content], [Version]) values (@p, @p, @p, @p);
insert into [OpenId_OpenIdScopeIndex] ([Name], [ScopeId], [DocumentId]) values (@p, @p, @p) RETURNING [Id];
insert into [PersonByName] ([Name], [DocumentId]) values (@p, @p) RETURNING [Id];
insert into [PersonByName] ([Name], [Group], [DocumentId]) values (@p, @p, @p) RETURNING [Id];
insert into [PersonByName] ([SomeName], [DocumentId]) values (@p, @p) RETURNING [Id];
insert into [PersonByNickname] ([Nickname], [DocumentId]) values (@p, @p) RETURNING [Id];
insert into [PersonIndex] ([Name], [Age], [DocumentId]) values (@p, @p, @p) RETURNING [Id];
insert into [RecordIndexingTask] ([CreatedUtc], [RecordId], [Category], [Type]) values (@p, @p, @p, @p);
insert into [tpArticleByPublishedDate] ([Title], [PublishedDateTime], [DocumentId]) values (@p, @p, @p) RETURNING [Id];
insert into [tpArticlesByDay] ([Count], [DayOfYear]) values (@p, @p) RETURNING [Id];
insert into [tpArticlesByDay_Document] ([ArticlesByDayId], [DocumentId]) values (@p, @p);
insert into [tpAttachmentByDay] ([Date], [Count]) values (@p, @p) RETURNING [Id];
insert into [tpAttachmentByDay_Document] ([AttachmentByDayId], [DocumentId]) values (@p, @p);
insert into [tpBinary] ([Content1], [Content2], [Content3], [Content4], [DocumentId]) values (@p, @p, @p, @p, @p) RETURNING [Id];
insert into [tpCarIndex] ([Name], [Category], [DocumentId]) values (@p, @p, @p) RETURNING [Id];
insert into [tpCol1_Document] ([Id], [Type], [Content], [Version]) values (@p, @p, @p, @p);
insert into [tpCol1_PersonByBothNamesCol] ([Firstname], [Lastname], [DocumentId]) values (@p, @p, @p) RETURNING [Id];
insert into [tpCol1_PersonByName] ([SomeName], [DocumentId]) values (@p, @p) RETURNING [Id];
insert into [tpCol1_PersonByNameCol] ([Name], [DocumentId]) values (@p, @p) RETURNING [Id];
insert into [tpCol1_PersonsByNameCol] ([Name], [Count]) values (@p, @p) RETURNING [Id];
insert into [tpCol1_PersonsByNameCol_Col1_Document] ([PersonsByNameColId], [DocumentId]) values (@p, @p);
insert into [tpDocument] ([Id], [Type], [Content], [Version]) values (@p, @p, @p, @p);
insert into [tpEmailByAttachment] ([Date], [AttachmentName], [DocumentId]) values (@p, @p, @p) RETURNING [Id];
insert into [tpPersonByAge] ([Name], [Age], [Adult], [DocumentId]) values (@p, @p, @p, @p) RETURNING [Id];
insert into [tpPersonByName] ([SomeName], [DocumentId]) values (@p, @p) RETURNING [Id];
insert into [tpPersonByNullableAge] ([Age], [DocumentId]) values (@p, @p) RETURNING [Id];
insert into [tpPersonIdentity] ([Identity], [DocumentId]) values (@p, @p) RETURNING [Id];
insert into [tpPublishedArticle] ([DocumentId]) values (@p) RETURNING [Id];
insert into [tpShapeIndex] ([Name], [DocumentId]) values (@p, @p) RETURNING [Id];
INSERT INTO [tpTable1] ([Column1]) VALUES('str')
insert into [tpTypesIndex] ([ValueBool], [ValueShort], [ValueInt], [ValueLong], [ValueFloat], [ValueDouble], [ValueDecimal], [ValueDateTime], [ValueDateTimeOffset], [ValueGuid], [ValueTimeSpan], [NullableBool], [NullableShort], [NullableInt], [NullableLong], [NullableFloat], [NullableDouble], [NullableDecimal], [NullableDateTime], [NullableDateTimeOffset], [NullableGuid], [NullableTimeSpan], [DocumentId]) values (@p, @p, @p, @p, @p, @p, @p, @p, @p, @p, @p, @p, @p, @p, @p, @p, @p, @p, @p, @p, @p, @p, @p) RETURNING [Id];
insert into [tpUserByRoleNameIndex] ([RoleName], [Count]) values (@p, @p) RETURNING [Id];
insert into [tpUserByRoleNameIndex_Document] ([UserByRoleNameIndexId], [DocumentId]) values (@p, @p);
insert into [UserByRoleNameIndex] ([RoleName], [Count]) values (@p, @p) RETURNING [Id];
insert into [UserByRoleNameIndex_Document] ([UserByRoleNameIndexId], [DocumentId]) values (@p, @p);
insert into [UserIndex] ([UserId], [NormalizedUserName], [NormalizedEmail], [IsEnabled], [IsLockoutEnabled], [LockoutEndUtc], [AccessFailedCount], [DocumentId]) values (@p, @p, @p, @p, @p, @p, @p, @p) RETURNING [Id];
renamecolumn [Table] [OnlyOneColumn]
renamecolumn [tpTable1] [Column1] [Column2]
select * from [ArticlesByDay] where [Day] = @p
SELECT * FROM [Document] LIMIT 1
select * from [Document] where [Id] = @p
select * from [Document] where [Id] IN (@p)
select * from [Document] where [Id] IN (@p0, @p1, @p2)
SELECT * FROM [OpenId_Document] LIMIT 1
SELECT * FROM [RecordIndexingTask] WHERE [Id] > @p AND [Category] = @p ORDER BY [Id]
SELECT * FROM [RecordIndexingTask] WHERE [Id] > @p AND [Category] = @p ORDER BY [Id] LIMIT 1
SELECT * FROM [tpArticleByPublishedDate] AS ArticleByPublishedDate_a1
SELECT * FROM [tpArticleByPublishedDate] AS ArticleByPublishedDate_a1 WHERE (ArticleByPublishedDate_a1.[PublishedDateTime] < @p)
select * from [tpArticlesByDay] where [DayOfYear] = @p
SELECT * FROM [tpAttachmentByDay] AS AttachmentByDay_a1 WHERE (AttachmentByDay_a1.[Date] = @p) LIMIT 1
select * from [tpAttachmentByDay] where [Date] = @p
SELECT * FROM [tpBinary] AS Binary_a1 LIMIT 1
SELECT * FROM [tpCarIndex] AS CarIndex_a1 WHERE (CarIndex_a1.[Category] = @p) LIMIT 1
SELECT * FROM [tpCol1_Document] LIMIT 1
select * from [tpCol1_Document] where [Id] = @p
select * from [tpCol1_Document] where [Id] IN (@p)
SELECT * FROM [tpCol1_PersonsByNameCol] AS PersonsByNameCol_a1 WHERE (PersonsByNameCol_a1.[Name] = @p) LIMIT 1
select * from [tpCol1_PersonsByNameCol] where [Name] = @p
SELECT * FROM [tpDocument] LIMIT 1
select * from [tpDocument] where [Id] = @p
select * from [tpDocument] where [Id] IN (@p)
select * from [tpDocument] where [Id] IN (@p0, @p1, @p2)
SELECT * FROM [tpPersonByAge] AS PersonByAge_a1 ORDER BY PersonByAge_a1.[Age]
SELECT * FROM [tpPersonByAge] AS PersonByAge_a1 ORDER BY PersonByAge_a1.[Age] DESC LIMIT 1
SELECT * FROM [tpPersonByAge] AS PersonByAge_a1 ORDER BY PersonByAge_a1.[Age] LIMIT 1
SELECT * FROM [tpPersonByName] AS PersonByName_a1 LIMIT 1
SELECT * FROM [tpPersonByName] AS PersonByName_a1 ORDER BY PersonByName_a1.[Id] LIMIT 1
SELECT * FROM [tpPersonByName] AS PersonByName_a1 ORDER BY PersonByName_a1.[Id] LIMIT 1 OFFSET 1
SELECT * FROM [tpPersonByName] AS PersonByName_a1 ORDER BY PersonByName_a1.[Id] OFFSET 1
SELECT * FROM [tpPersonByName] AS PersonByName_a1 ORDER BY PersonByName_a1.[SomeName] DESC LIMIT 1
SELECT * FROM [tpPersonByName] AS PersonByName_a1 ORDER BY PersonByName_a1.[SomeName] DESC LIMIT 1 OFFSET 1
SELECT * FROM [tpPersonByName] AS PersonByName_a1 ORDER BY PersonByName_a1.[SomeName] DESC OFFSET 1
SELECT * FROM [tpPersonByName] AS PersonByName_a1 ORDER BY PersonByName_a1.[SomeName] LIMIT 1
SELECT * FROM [tpPersonByName] AS PersonByName_a1 ORDER BY PersonByName_a1.[SomeName] LIMIT 1 OFFSET 1
SELECT * FROM [tpPersonByName] AS PersonByName_a1 ORDER BY PersonByName_a1.[SomeName] OFFSET 1
SELECT * FROM [tpPersonByName] AS PersonByName_a1 WHERE (PersonByName_a1.[SomeName] = @p)
SELECT * FROM [tpPersonByName] AS PersonByName_a1 WHERE (PersonByName_a1.[SomeName] = @p) LIMIT 1
SELECT * FROM [tpPersonByName] AS PersonByName_a1 WHERE [SomeName] = @p LIMIT 1
SELECT * FROM [tpPersonByName] AS PersonByName_a1 WHERE ThisColumnDoesNotExist = 1
SELECT * FROM [tpTypesIndex] AS TypesIndex_a1 LIMIT 1
SELECT * FROM [tpTypesIndex] AS TypesIndex_a1 WHERE (((((TypesIndex_a1.[ValueBool] = @p) and (TypesIndex_a1.[ValueDateTime] = @p)) and (TypesIndex_a1.[ValueDateTimeOffset] = @p)) and (TypesIndex_a1.[ValueTimeSpan] = @p)) and (TypesIndex_a1.[ValueGuid] = @p)) LIMIT 1
SELECT * FROM [tpTypesIndex] AS TypesIndex_a1 WHERE (TypesIndex_a1.[ValueDateTime] = @p) LIMIT 1
SELECT * FROM [tpTypesIndex] AS TypesIndex_a1 WHERE (TypesIndex_a1.[ValueDateTimeOffset] = @p) LIMIT 1
SELECT * FROM [tpUserByRoleNameIndex] AS UserByRoleNameIndex_a1 WHERE (@p = @p or (UserByRoleNameIndex_a1.[RoleName] IS NULL)) LIMIT 1
SELECT * FROM [tpUserByRoleNameIndex] AS UserByRoleNameIndex_a1 WHERE (@p = null or (UserByRoleNameIndex_a1.[RoleName] IS NULL)) LIMIT 1
SELECT * FROM [tpUserByRoleNameIndex] AS UserByRoleNameIndex_a1 WHERE (null = @p or (UserByRoleNameIndex_a1.[RoleName] IS NULL)) LIMIT 1
select * from [tpUserByRoleNameIndex] where [RoleName] = @p
select * from [UserByRoleNameIndex] where [RoleName] = @p
SELECT [Column1] FROM [tpTable1]
SELECT [Column2] FROM [tpTable1]
SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id] FROM [Document] INNER JOIN [IndexProfileIndex] AS IndexProfileIndex_a1 ON IndexProfileIndex_a1.[DocumentId] = [Document].[Id] GROUP BY [Document].[Id]) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id]
SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id] FROM [Document] INNER JOIN [PersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [Document].[Id] WHERE (PersonByName_a1.[Name] = @p) GROUP BY [Document].[Id]) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id]
SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id] FROM [Document] INNER JOIN [PersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [Document].[Id] WHERE (PersonByName_a1.[SomeName] = @p) GROUP BY [Document].[Id]) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id]
SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id] FROM [Document] INNER JOIN [PersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [Document].[Id] WHERE [Document].[Type] = @p AND ThisColumnDoesNotExist = 1 GROUP BY [Document].[Id]) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id]
SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id] FROM [Document] INNER JOIN [PersonByNickname] AS PersonByNickname_a1 ON PersonByNickname_a1.[DocumentId] = [Document].[Id] WHERE PersonByNickname_a1.[Nickname] IN (SELECT PersonByName_a1.[Name] FROM [PersonByName] AS PersonByName_a1 WHERE (PersonByName_a1.[Name] <> @p)) GROUP BY [Document].[Id]) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id]
SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id] FROM [Document] INNER JOIN [PersonIndex] AS PersonIndex_a1 ON PersonIndex_a1.[DocumentId] = [Document].[Id] WHERE (PersonIndex_a1.[Age] > @p) GROUP BY [Document].[Id]) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id]
SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id] FROM [Document] INNER JOIN [UserByRoleNameIndex_Document] AS UserByRoleNameIndex_Document_a1 ON UserByRoleNameIndex_Document_a1.[DocumentId] = [Document].[Id] INNER JOIN [UserByRoleNameIndex] AS UserByRoleNameIndex_a1 ON UserByRoleNameIndex_a1.[Id] = UserByRoleNameIndex_Document_a1.[UserByRoleNameIndexId] WHERE (UserByRoleNameIndex_a1.[RoleName] = @p) GROUP BY [Document].[Id]) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id]
SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id], MAX(ArticlesByDay_a1.[Count]) AS order_1 FROM [Document] INNER JOIN [ArticlesByDay_Document] AS ArticlesByDay_Document_a1 ON ArticlesByDay_Document_a1.[DocumentId] = [Document].[Id] INNER JOIN [ArticlesByDay] AS ArticlesByDay_a1 ON ArticlesByDay_a1.[Id] = ArticlesByDay_Document_a1.[ArticlesByDayId] GROUP BY [Document].[Id] ORDER BY order_1 DESC OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id] ORDER BY order_1 DESC
SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id], MAX(ArticlesByDay_a1.[Count]) AS order_1 FROM [Document] INNER JOIN [ArticlesByDay_Document] AS ArticlesByDay_Document_a1 ON ArticlesByDay_Document_a1.[DocumentId] = [Document].[Id] INNER JOIN [ArticlesByDay] AS ArticlesByDay_a1 ON ArticlesByDay_a1.[Id] = ArticlesByDay_Document_a1.[ArticlesByDayId] GROUP BY [Document].[Id] ORDER BY order_1 LIMIT 1 OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id] ORDER BY order_1
SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id], MAX(ArticlesByDay_a1.[Count]) AS order_1 FROM [Document] INNER JOIN [ArticlesByDay_Document] AS ArticlesByDay_Document_a1 ON ArticlesByDay_Document_a1.[DocumentId] = [Document].[Id] INNER JOIN [ArticlesByDay] AS ArticlesByDay_a1 ON ArticlesByDay_a1.[Id] = ArticlesByDay_Document_a1.[ArticlesByDayId] GROUP BY [Document].[Id] ORDER BY order_1 OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id] ORDER BY order_1
SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id], MAX(GetCurrentTimestamp()) AS order_1 FROM [Document] INNER JOIN [PersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [Document].[Id] GROUP BY [Document].[Id] ORDER BY order_1 LIMIT 1) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id] ORDER BY order_1
SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id], MAX(GetCurrentTimestamp()) AS order_1 FROM [Document] INNER JOIN [PersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [Document].[Id] GROUP BY [Document].[Id] ORDER BY order_1 OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id] ORDER BY order_1
SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id], MAX(PersonByName_a1.[Group]) AS order_1, MAX(GetCurrentTimestamp()) AS order_3 FROM [Document] INNER JOIN [PersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [Document].[Id] GROUP BY [Document].[Id] ORDER BY order_1, order_3 OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id] ORDER BY order_1, order_3
SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id], MAX(PersonByName_a1.[Name]) AS order_1 FROM [Document] INNER JOIN [PersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [Document].[Id] GROUP BY [Document].[Id] ORDER BY order_1 DESC LIMIT 1 OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id] ORDER BY order_1 DESC
SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id], MAX(PersonByName_a1.[Name]) AS order_1 FROM [Document] INNER JOIN [PersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [Document].[Id] GROUP BY [Document].[Id] ORDER BY order_1 LIMIT 1 OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id] ORDER BY order_1
SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id], MAX(PersonByName_a1.[Name]) AS order_1 FROM [Document] INNER JOIN [PersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [Document].[Id] GROUP BY [Document].[Id] ORDER BY order_1 OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id] ORDER BY order_1
SELECT [Document].* FROM [Document] INNER JOIN (SELECT [Document].[Id], MAX(PersonIndex_a1.[Name]) AS order_1 FROM [Document] INNER JOIN [PersonIndex] AS PersonIndex_a1 ON PersonIndex_a1.[DocumentId] = [Document].[Id] GROUP BY [Document].[Id] ORDER BY order_1 OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [Document].[Id] ORDER BY order_1
SELECT [Document].* FROM [Document] INNER JOIN [PersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [Document].[Id] WHERE (PersonByName_a1.[SomeName] = @p) LIMIT 1
SELECT [Document].* FROM [Document] INNER JOIN [UserIndex] AS UserIndex_a1 ON UserIndex_a1.[DocumentId] = [Document].[Id] WHERE (UserIndex_a1.[NormalizedEmail] = @p) LIMIT 1
SELECT [Document].* FROM [Document] INNER JOIN [UserIndex] AS UserIndex_a1 ON UserIndex_a1.[DocumentId] = [Document].[Id] WHERE (UserIndex_a1.[NormalizedUserName] = @p) LIMIT 1
SELECT [Document].* FROM [Document] WHERE [Document].[Type] = @p LIMIT 1
SELECT [OpenId_Document].* FROM [OpenId_Document] INNER JOIN (SELECT [OpenId_Document].[Id], MAX([OpenId_Document].[Id]) AS order_1 FROM [OpenId_Document] WHERE [OpenId_Document].[Type] = @p GROUP BY [OpenId_Document].[Id] ORDER BY order_1 LIMIT 1 OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [OpenId_Document].[Id] ORDER BY order_1
SELECT [OpenId_Document].* FROM [OpenId_Document] INNER JOIN [OpenId_OpenIdScopeIndex] AS OpenIdScopeIndex_a1 ON OpenIdScopeIndex_a1.[DocumentId] = [OpenId_Document].[Id] WHERE (OpenIdScopeIndex_a1.[Name] = @p) LIMIT 1
SELECT [tpCol1_Document].* FROM [tpCol1_Document] WHERE [tpCol1_Document].[Type] = @p LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id] FROM [tpDocument] GROUP BY [tpDocument].[Id]) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id]
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id] FROM [tpDocument] INNER JOIN [tpArticlesByDay_Document] AS ArticlesByDay_Document_a1 ON ArticlesByDay_Document_a1.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticlesByDay] AS ArticlesByDay_a1 ON ArticlesByDay_a1.[Id] = ArticlesByDay_Document_a1.[ArticlesByDayId] WHERE ((ArticlesByDay_a1.[DayOfYear] = @p) or (ArticlesByDay_a1.[DayOfYear] = @p)) GROUP BY [tpDocument].[Id]) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id]
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id] FROM [tpDocument] INNER JOIN [tpArticlesByDay_Document] AS ArticlesByDay_Document_a1 ON ArticlesByDay_Document_a1.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticlesByDay] AS ArticlesByDay_a1 ON ArticlesByDay_a1.[Id] = ArticlesByDay_Document_a1.[ArticlesByDayId] WHERE (ArticlesByDay_a1.[DayOfYear] = @p) GROUP BY [tpDocument].[Id]) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id]
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id] FROM [tpDocument] INNER JOIN [tpEmailByAttachment] AS EmailByAttachment_a1 ON EmailByAttachment_a1.[DocumentId] = [tpDocument].[Id] WHERE (EmailByAttachment_a1.[AttachmentName] like @p) GROUP BY [tpDocument].[Id]) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id]
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id] FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE ((PersonByAge_a1.[Age] = @p) or (PersonByAge_a1.[Name] IS NULL)) GROUP BY [tpDocument].[Id]) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id]
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id] FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] GROUP BY [tpDocument].[Id]) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id]
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id] FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND (PersonByName_a1.[SomeName] = @p) GROUP BY [tpDocument].[Id]) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id]
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id] FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND ThisColumnDoesNotExist = 1 GROUP BY [tpDocument].[Id]) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id]
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id] FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p GROUP BY [tpDocument].[Id]) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id]
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id] FROM [tpDocument] INNER JOIN [tpShapeIndex] AS ShapeIndex_a1 ON ShapeIndex_a1.[DocumentId] = [tpDocument].[Id] GROUP BY [tpDocument].[Id]) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id]
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id] FROM [tpDocument] WHERE [tpDocument].[Type] = @p GROUP BY [tpDocument].[Id]) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id]
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id], MAX([tpDocument].[Id]) AS order_1 FROM [tpDocument] INNER JOIN [tpEmailByAttachment] AS EmailByAttachment_a1 ON EmailByAttachment_a1.[DocumentId] = [tpDocument].[Id] WHERE (EmailByAttachment_a1.[AttachmentName] like @p) GROUP BY [tpDocument].[Id] ORDER BY order_1 LIMIT 1 OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id] ORDER BY order_1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id], MAX([tpDocument].[Id]) AS order_1 FROM [tpDocument] INNER JOIN [tpEmailByAttachment] AS EmailByAttachment_a1 ON EmailByAttachment_a1.[DocumentId] = [tpDocument].[Id] WHERE (EmailByAttachment_a1.[AttachmentName] like @p) GROUP BY [tpDocument].[Id] ORDER BY order_1 LIMIT 1) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id] ORDER BY order_1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id], MAX([tpDocument].[Id]) AS order_1 FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] GROUP BY [tpDocument].[Id] ORDER BY order_1 LIMIT 1) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id] ORDER BY order_1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id], MAX([tpDocument].[Id]) AS order_1 FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] GROUP BY [tpDocument].[Id] ORDER BY order_1 LIMIT 1) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id] ORDER BY order_1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id], MAX([tpDocument].[Id]) AS order_1 FROM [tpDocument] WHERE [tpDocument].[Type] = @p GROUP BY [tpDocument].[Id] ORDER BY order_1 LIMIT 1 OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id] ORDER BY order_1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id], MAX([tpDocument].[Id]) AS order_1 FROM [tpDocument] WHERE [tpDocument].[Type] = @p GROUP BY [tpDocument].[Id] ORDER BY order_1 LIMIT 1) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id] ORDER BY order_1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id], MAX([tpDocument].[Id]) AS order_1 FROM [tpDocument] WHERE [tpDocument].[Type] = @p GROUP BY [tpDocument].[Id] ORDER BY order_1 OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id] ORDER BY order_1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id], MAX(GetCurrentTimestamp()) AS order_1 FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] GROUP BY [tpDocument].[Id] ORDER BY order_1 OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id] ORDER BY order_1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id], MAX(PersonByAge_a1.[Age]) AS order_1 FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] GROUP BY [tpDocument].[Id] ORDER BY order_1 DESC OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id] ORDER BY order_1 DESC
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id], MAX(PersonByAge_a1.[Age]) AS order_1 FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] GROUP BY [tpDocument].[Id] ORDER BY order_1 OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id] ORDER BY order_1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id], MAX(PersonByAge_a1.[Age]) AS order_1 FROM [tpDocument] INNER JOIN [tpPersonIdentity] AS PersonIdentity_a1 ON PersonIdentity_a1.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND (PersonIdentity_a1.[Identity] = @p) GROUP BY [tpDocument].[Id] ORDER BY order_1 OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id] ORDER BY order_1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id], MAX(PersonByName_a1.[DocumentId]) AS order_1 FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] GROUP BY [tpDocument].[Id] ORDER BY order_1 LIMIT 1 OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id] ORDER BY order_1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id], MAX(PersonByName_a1.[SomeName]) AS order_1 FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] GROUP BY [tpDocument].[Id] ORDER BY order_1 OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id] ORDER BY order_1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN (SELECT [tpDocument].[Id], MAX(PersonByName_a1.[SomeName]) AS order_1, MAX(GetCurrentTimestamp()) AS order_3 FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] GROUP BY [tpDocument].[Id] ORDER BY order_1, order_3 OFFSET 1) AS IndexQuery ON IndexQuery.[Id] = [tpDocument].[Id] ORDER BY order_1, order_3
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a1 ON ArticleByPublishedDate_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND (ArticleByPublishedDate_a1.[Title] like @p) LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a2 ON ArticleByPublishedDate_a2.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a3 ON ArticleByPublishedDate_a3.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND ((ArticleByPublishedDate_a2.[Title] like @p) AND ArticleByPublishedDate_a3.[Title] NOT IN (SELECT ArticleByPublishedDate_a3.[Title] FROM [tpArticleByPublishedDate] AS ArticleByPublishedDate_a3 WHERE (ArticleByPublishedDate_a3.[Title] like @p)) ) ORDER BY ArticleByPublishedDate_a3.[Title] DESC LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a2 ON ArticleByPublishedDate_a2.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a3 ON ArticleByPublishedDate_a3.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND (ArticleByPublishedDate_a2.[Title] NOT IN (SELECT ArticleByPublishedDate_a2.[Title] FROM [tpArticleByPublishedDate] AS ArticleByPublishedDate_a2 WHERE (ArticleByPublishedDate_a2.[Title] like @p)) AND (ArticleByPublishedDate_a3.[Title] like @p)) ORDER BY ArticleByPublishedDate_a2.[Title] DESC LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpArticlesByDay_Document] AS ArticlesByDay_Document_a1 ON ArticlesByDay_Document_a1.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticlesByDay] AS ArticlesByDay_a1 ON ArticlesByDay_a1.[Id] = ArticlesByDay_Document_a1.[ArticlesByDayId] WHERE (ArticlesByDay_a1.[DayOfYear] = @p) LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpAttachmentByDay_Document] AS AttachmentByDay_Document_a1 ON AttachmentByDay_Document_a1.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpAttachmentByDay] AS AttachmentByDay_a1 ON AttachmentByDay_a1.[Id] = AttachmentByDay_Document_a1.[AttachmentByDayId] WHERE (AttachmentByDay_a1.[Date] = @p) LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpCarIndex] AS CarIndex_a1 ON CarIndex_a1.[DocumentId] = [tpDocument].[Id] WHERE (CarIndex_a1.[Category] = @p) LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE ((PersonByAge_a1.[Adult] = @p) and 1 = 1) LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE ((PersonByAge_a1.[Adult] = @p) and PersonByAge_a1.[Name] = @p) LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE ((PersonByAge_a1.[Adult] = @p) and PersonByAge_a1.[Name] IN (@p0, @p1, @p2) ) LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE ((PersonByAge_a1.[Age] = @p) or (PersonByAge_a1.[Name] IS NULL)) LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE ((PersonByAge_a1.[Name] = @p) and (PersonByAge_a1.[Adult] = @p)) LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE PersonByAge_a1.[Age] = @p LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE PersonByAge_a1.[Age] IN (@p0, @p1, @p2) LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE PersonByAge_a1.[Age] NOT IN (@p0, @p1, @p2) LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND ((PersonByName_a1.[SomeName] like @p) AND (PersonByAge_a1.[Age] = @p) AND (PersonByName_a1.[SomeName] like @p)) LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND ((PersonByName_a1.[SomeName] like @p) AND (PersonByAge_a1.[Age] = @p)) ORDER BY PersonByName_a1.[SomeName] DESC LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND ((PersonByName_a1.[SomeName] like @p) AND (PersonByAge_a1.[Age] = @p)) ORDER BY PersonByName_a1.[SomeName] LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] ORDER BY PersonByName_a1.[SomeName]
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] ORDER BY PersonByName_a1.[SomeName] DESC LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] WHERE (PersonByName_a1.[SomeName] = @p) LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] WHERE [SomeName] = 'str' LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] WHERE [SomeName] = @p LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] INNER JOIN [tpPersonIdentity] AS PersonIdentity_a1 ON PersonIdentity_a1.[DocumentId] = [tpDocument].[Id] WHERE (PersonIdentity_a1.[Identity] = @p) LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] LIMIT 1
SELECT [tpDocument].* FROM [tpDocument] WHERE [tpDocument].[Type] = @p LIMIT 1
SELECT count(*) FROM [Document] WHERE [Document].[Type] = @p
SELECT count(*) FROM [tpArticleByPublishedDate] AS ArticleByPublishedDate_a1
SELECT count(*) FROM [tpArticleByPublishedDate] AS ArticleByPublishedDate_a1 WHERE (ArticleByPublishedDate_a1.[PublishedDateTime] = @p)
SELECT count(*) FROM [tpArticlesByDay] AS ArticlesByDay_a1
SELECT count(*) FROM [tpArticlesByDay] AS ArticlesByDay_a1 WHERE (ArticlesByDay_a1.[DayOfYear] = @p)
SELECT count(*) FROM [tpAttachmentByDay] AS AttachmentByDay_a1
SELECT count(*) FROM [tpCol1_Document]
SELECT count(*) FROM [tpCol1_PersonByName] AS PersonByName_a1
SELECT count(*) FROM [tpDocument]
SELECT count(*) FROM [tpDocument] WHERE [tpDocument].[Type] = @p
SELECT count(*) FROM [tpPersonByAge] AS PersonByAge_a1
SELECT count(*) FROM [tpPersonByAge] AS PersonByAge_a1 WHERE ((PersonByAge_a1.[Adult] = @p) and (PersonByAge_a1.[Adult] = @p))
SELECT count(*) FROM [tpPersonByAge] AS PersonByAge_a1 WHERE (PersonByAge_a1.[Adult] <> @p)
SELECT count(*) FROM [tpPersonByAge] AS PersonByAge_a1 WHERE (PersonByAge_a1.[Adult] = @p)
SELECT count(*) FROM [tpPersonByAge] AS PersonByAge_a1 WHERE (PersonByAge_a1.[Age] = @p)
SELECT count(*) FROM [tpPersonByName] AS PersonByName_a1
SELECT count(*) FROM [tpPersonByName] AS PersonByName_a1 WHERE (PersonByName_a1.[SomeName] = @p)
SELECT count(*) FROM [tpPersonByName] AS PersonByName_a1 WHERE (PersonByName_a1.[SomeName] IS NOT NULL)
SELECT count(*) FROM [tpPersonByName] AS PersonByName_a1 WHERE (PersonByName_a1.[SomeName] IS NULL)
SELECT count(*) FROM [tpPersonByName] AS PersonByName_a1 WHERE @p = @p
SELECT count(*) FROM [tpPersonByNameCol] AS PersonByNameCol_a1
SELECT count(*) FROM [tpPersonIdentity] AS PersonIdentity_a1
SELECT count(*) FROM [tpPersonIdentity] AS PersonIdentity_a1 WHERE (PersonIdentity_a1.[Identity] = @p)
SELECT count(*) FROM [tpShapeIndex] AS ShapeIndex_a1
SELECT count(*) FROM [tpUserByRoleNameIndex] AS UserByRoleNameIndex_a1
SELECT count(*) FROM [UserIndex] AS UserIndex_a1 WHERE (UserIndex_a1.[UserId] = @p)
SELECT count(1) FROM [tpArticleByPublishedDate] WHERE [PublishedDateTime] < GetCurrentDateTime()
SELECT count(1) FROM [tpArticleByPublishedDate] WHERE [PublishedDateTime] > GetCurrentDateTime()
SELECT count(1) FROM [tpDocument] AS d INNER JOIN [tpArticleByPublishedDate] AS a ON a.[DocumentId] = d.[Id]
SELECT count(1) FROM [tpDocument] AS d LEFT JOIN [tpArticleByPublishedDate] AS a ON a.[DocumentId] = d.[Id]
SELECT count(1) FROM [tpDocument] AS d RIGHT JOIN [tpArticleByPublishedDate] AS a ON a.[DocumentId] = d.[Id]
SELECT count(distinct [Document].[Id]) FROM [Document] INNER JOIN [PersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [Document].[Id] WHERE (PersonByName_a1.[Name] = @p)
SELECT count(distinct [Document].[Id]) FROM [Document] INNER JOIN [PersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [Document].[Id] WHERE (PersonByName_a1.[Name] like @p)
SELECT count(distinct [Document].[Id]) FROM [Document] INNER JOIN [PersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [Document].[Id] WHERE (PersonByName_a1.[SomeName] = @p)
SELECT count(distinct [Document].[Id]) FROM [Document] INNER JOIN [PersonByNickname] AS PersonByNickname_a1 ON PersonByNickname_a1.[DocumentId] = [Document].[Id] WHERE PersonByNickname_a1.[Nickname] IN (SELECT PersonByName_a1.[Name] FROM [PersonByName] AS PersonByName_a1 WHERE (PersonByName_a1.[Name] = @p))
SELECT count(distinct [Document].[Id]) FROM [Document] INNER JOIN [PersonIndex] AS PersonIndex_a1 ON PersonIndex_a1.[DocumentId] = [Document].[Id] WHERE (PersonIndex_a1.[Name] = @p)
SELECT count(distinct [tpCol1_Document].[Id]) FROM [tpCol1_Document] INNER JOIN [tpCol1_PersonByBothNamesCol] AS PersonByBothNamesCol_a1 ON PersonByBothNamesCol_a1.[DocumentId] = [tpCol1_Document].[Id] WHERE (PersonByBothNamesCol_a1.[Lastname] = @p)
SELECT count(distinct [tpCol1_Document].[Id]) FROM [tpCol1_Document] INNER JOIN [tpCol1_PersonByNameCol] AS PersonByNameCol_a1 ON PersonByNameCol_a1.[DocumentId] = [tpCol1_Document].[Id]
SELECT count(distinct [tpCol1_Document].[Id]) FROM [tpCol1_Document] INNER JOIN [tpCol1_PersonByNameCol] AS PersonByNameCol_a1 ON PersonByNameCol_a1.[DocumentId] = [tpCol1_Document].[Id] WHERE (PersonByNameCol_a1.[Name] = @p)
SELECT count(distinct [tpCol1_Document].[Id]) FROM [tpCol1_Document] INNER JOIN [tpCol1_PersonByNameCol] AS PersonByNameCol_a1 ON PersonByNameCol_a1.[DocumentId] = [tpCol1_Document].[Id] WHERE [tpCol1_Document].[Type] = @p AND PersonByNameCol_a1.[Name] IN (SELECT PersonByBothNamesCol_a1.[Firstname] FROM [tpCol1_PersonByBothNamesCol] AS PersonByBothNamesCol_a1)
SELECT count(distinct [tpCol1_Document].[Id]) FROM [tpCol1_Document] INNER JOIN [tpCol1_PersonByNameCol] AS PersonByNameCol_a1 ON PersonByNameCol_a1.[DocumentId] = [tpCol1_Document].[Id] WHERE PersonByNameCol_a1.[Name] IN (SELECT PersonByBothNamesCol_a1.[Firstname] FROM [tpCol1_PersonByBothNamesCol] AS PersonByBothNamesCol_a1 WHERE (PersonByBothNamesCol_a1.[Lastname] like @p))
SELECT count(distinct [tpCol1_Document].[Id]) FROM [tpCol1_Document] INNER JOIN [tpCol1_PersonByNameCol] AS PersonByNameCol_a1 ON PersonByNameCol_a1.[DocumentId] = [tpCol1_Document].[Id] WHERE PersonByNameCol_a1.[Name] IN (SELECT PersonByBothNamesCol_a1.[Firstname] FROM [tpCol1_PersonByBothNamesCol] AS PersonByBothNamesCol_a1)
SELECT count(distinct [tpCol1_Document].[Id]) FROM [tpCol1_Document] INNER JOIN [tpCol1_PersonByNameCol] AS PersonByNameCol_a1 ON PersonByNameCol_a1.[DocumentId] = [tpCol1_Document].[Id] WHERE PersonByNameCol_a1.[Name] NOT IN (SELECT PersonByBothNamesCol_a1.[Firstname] FROM [tpCol1_PersonByBothNamesCol] AS PersonByBothNamesCol_a1 WHERE ((PersonByBothNamesCol_a1.[Lastname] like @p) or (PersonByBothNamesCol_a1.[Lastname] like @p)))
SELECT count(distinct [tpCol1_Document].[Id]) FROM [tpCol1_Document] INNER JOIN [tpCol1_PersonByNameCol] AS PersonByNameCol_a1 ON PersonByNameCol_a1.[DocumentId] = [tpCol1_Document].[Id] WHERE PersonByNameCol_a1.[Name] NOT IN (SELECT PersonByBothNamesCol_a1.[Firstname] FROM [tpCol1_PersonByBothNamesCol] AS PersonByBothNamesCol_a1)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a1 ON ArticleByPublishedDate_a1.[DocumentId] = [tpDocument].[Id] WHERE ((ArticleByPublishedDate_a1.[Title] <> @p) and (ArticleByPublishedDate_a1.[PublishedDateTime] < @p))
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a1 ON ArticleByPublishedDate_a1.[DocumentId] = [tpDocument].[Id] WHERE (ArticleByPublishedDate_a1.[PublishedDateTime] < @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a1 ON ArticleByPublishedDate_a1.[DocumentId] = [tpDocument].[Id] WHERE (ArticleByPublishedDate_a1.[Title] <> @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a1 ON ArticleByPublishedDate_a1.[DocumentId] = [tpDocument].[Id] WHERE (ArticleByPublishedDate_a1.[Title] = @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a1 ON ArticleByPublishedDate_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND (ArticleByPublishedDate_a1.[Title] like @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a2 ON ArticleByPublishedDate_a2.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a3 ON ArticleByPublishedDate_a3.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND ((ArticleByPublishedDate_a2.[Title] like @p) AND (ArticleByPublishedDate_a3.[Title] like @p))
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a2 ON ArticleByPublishedDate_a2.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a3 ON ArticleByPublishedDate_a3.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND ((ArticleByPublishedDate_a2.[Title] like @p) AND ArticleByPublishedDate_a3.[Title] NOT IN (SELECT ArticleByPublishedDate_a3.[Title] FROM [tpArticleByPublishedDate] AS ArticleByPublishedDate_a3 WHERE (ArticleByPublishedDate_a3.[Title] like @p)) )
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a2 ON ArticleByPublishedDate_a2.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a3 ON ArticleByPublishedDate_a3.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND ((ArticleByPublishedDate_a2.[Title] like @p) OR(ArticleByPublishedDate_a3.[Title] like @p))
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a2 ON ArticleByPublishedDate_a2.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a3 ON ArticleByPublishedDate_a3.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND (ArticleByPublishedDate_a2.[Title] NOT IN (SELECT ArticleByPublishedDate_a2.[Title] FROM [tpArticleByPublishedDate] AS ArticleByPublishedDate_a2 WHERE (ArticleByPublishedDate_a2.[Title] like @p)) AND (ArticleByPublishedDate_a3.[Title] like @p))
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a2 ON ArticleByPublishedDate_a2.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND ArticleByPublishedDate_a2.[Title] NOT IN (SELECT ArticleByPublishedDate_a2.[Title] FROM [tpArticleByPublishedDate] AS ArticleByPublishedDate_a2 WHERE (ArticleByPublishedDate_a2.[Title] like @p))
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a3 ON ArticleByPublishedDate_a3.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a4 ON ArticleByPublishedDate_a4.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a6 ON ArticleByPublishedDate_a6.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a7 ON ArticleByPublishedDate_a7.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a8 ON ArticleByPublishedDate_a8.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND ( ( ((ArticleByPublishedDate_a3.[Title] like @p) AND (ArticleByPublishedDate_a4.[Title] like @p)) OR ((ArticleByPublishedDate_a6.[Title] like @p) AND (ArticleByPublishedDate_a7.[Title] like @p))) AND ArticleByPublishedDate_a8.[Title] NOT IN (SELECT ArticleByPublishedDate_a8.[Title] FROM [tpArticleByPublishedDate] AS ArticleByPublishedDate_a8 WHERE (ArticleByPublishedDate_a8.[Title] like @p)) )
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a3 ON ArticleByPublishedDate_a3.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a4 ON ArticleByPublishedDate_a4.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a6 ON ArticleByPublishedDate_a6.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a7 ON ArticleByPublishedDate_a7.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND ( ((ArticleByPublishedDate_a3.[Title] like @p) AND (ArticleByPublishedDate_a4.[Title] like @p)) OR ((ArticleByPublishedDate_a6.[Title] like @p) AND (ArticleByPublishedDate_a7.[Title] like @p)))
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a4 ON ArticleByPublishedDate_a4.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a5 ON ArticleByPublishedDate_a5.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a7 ON ArticleByPublishedDate_a7.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a8 ON ArticleByPublishedDate_a8.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticleByPublishedDate] AS ArticleByPublishedDate_a9 ON ArticleByPublishedDate_a9.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND ( ( ((ArticleByPublishedDate_a4.[Title] like @p) AND (ArticleByPublishedDate_a5.[Title] like @p)) OR ((ArticleByPublishedDate_a7.[Title] like @p) AND (ArticleByPublishedDate_a8.[Title] like @p))) AND ArticleByPublishedDate_a9.[Title] NOT IN (SELECT ArticleByPublishedDate_a9.[Title] FROM [tpArticleByPublishedDate] AS ArticleByPublishedDate_a9 WHERE (ArticleByPublishedDate_a9.[Title] like @p)) )
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpArticlesByDay_Document] AS ArticlesByDay_Document_a1 ON ArticlesByDay_Document_a1.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticlesByDay] AS ArticlesByDay_a1 ON ArticlesByDay_a1.[Id] = ArticlesByDay_Document_a1.[ArticlesByDayId] WHERE ((ArticlesByDay_a1.[DayOfYear] = @p) or (ArticlesByDay_a1.[DayOfYear] = @p))
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpArticlesByDay_Document] AS ArticlesByDay_Document_a1 ON ArticlesByDay_Document_a1.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticlesByDay] AS ArticlesByDay_a1 ON ArticlesByDay_a1.[Id] = ArticlesByDay_Document_a1.[ArticlesByDayId] WHERE (ArticlesByDay_a1.[DayOfYear] = @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpArticlesByDay_Document] AS ArticlesByDay_Document_a1 ON ArticlesByDay_Document_a1.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticlesByDay] AS ArticlesByDay_a1 ON ArticlesByDay_a1.[Id] = ArticlesByDay_Document_a1.[ArticlesByDayId] WHERE [tpDocument].[Type] = @p
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpArticlesByDay_Document] AS ArticlesByDay_Document_a1 ON ArticlesByDay_Document_a1.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticlesByDay] AS ArticlesByDay_a1 ON ArticlesByDay_a1.[Id] = ArticlesByDay_Document_a1.[ArticlesByDayId] WHERE [tpDocument].[Type] = @p AND (ArticlesByDay_a1.[DayOfYear] = @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpArticlesByDay_Document] AS ArticlesByDay_Document_a2 ON ArticlesByDay_Document_a2.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticlesByDay] AS ArticlesByDay_a2 ON ArticlesByDay_a2.[Id] = ArticlesByDay_Document_a2.[ArticlesByDayId] INNER JOIN [tpArticlesByDay_Document] AS ArticlesByDay_Document_a3 ON ArticlesByDay_Document_a3.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticlesByDay] AS ArticlesByDay_a3 ON ArticlesByDay_a3.[Id] = ArticlesByDay_Document_a3.[ArticlesByDayId] WHERE [tpDocument].[Type] = @p AND ((ArticlesByDay_a2.[DayOfYear] = @p) OR(ArticlesByDay_a3.[DayOfYear] = @p))
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpEmailByAttachment] AS EmailByAttachment_a1 ON EmailByAttachment_a1.[DocumentId] = [tpDocument].[Id] WHERE (EmailByAttachment_a1.[AttachmentName] like @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE (((PersonByAge_a1.[Name] || @p) || PersonByAge_a1.[Name]) = @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE ((PersonByAge_a1.[Age] = @p) or (PersonByAge_a1.[Name] = @p))
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE ((PersonByAge_a1.[Age] = @p) or (PersonByAge_a1.[Name] IS NULL))
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE ((PersonByAge_a1.[Name] || PersonByAge_a1.[Name] || PersonByAge_a1.[Name] || PersonByAge_a1.[Name]) = @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE ((PersonByAge_a1.[Name] || PersonByAge_a1.[Name] || PersonByAge_a1.[Name]) = @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE ((PersonByAge_a1.[Name] || PersonByAge_a1.[Name]) = @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE ((PersonByAge_a1.[Name] || PersonByAge_a1.[Name]) like @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE (PersonByAge_a1.[Name] like @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE (PersonByAge_a1.[Name] not like @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE PersonByAge_a1.[Name] IN (SELECT PersonByName_a1.[SomeName] FROM [tpPersonByName] AS PersonByName_a1 WHERE ((PersonByName_a1.[SomeName] like @p) or (PersonByName_a1.[SomeName] like @p)))
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE PersonByAge_a1.[Name] IN (SELECT PersonByName_a1.[SomeName] FROM [tpPersonByName] AS PersonByName_a1 WHERE @p = @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE PersonByAge_a1.[Name] IN (SELECT PersonByName_a1.[SomeName] FROM [tpPersonByName] AS PersonByName_a1)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE PersonByAge_a1.[Name] NOT IN (SELECT PersonByName_a1.[SomeName] FROM [tpPersonByName] AS PersonByName_a1 WHERE ((PersonByName_a1.[SomeName] like @p) or (PersonByName_a1.[SomeName] like @p)))
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE PersonByAge_a1.[Name] NOT IN (SELECT PersonByName_a1.[SomeName] FROM [tpPersonByName] AS PersonByName_a1 WHERE @p = @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE PersonByAge_a1.[Name] NOT IN (SELECT PersonByName_a1.[SomeName] FROM [tpPersonByName] AS PersonByName_a1)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id]
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND ((PersonByName_a1.[SomeName] = @p) AND (PersonByAge_a1.[Age] = @p))
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpPersonByAge] AS PersonByAge_a1 ON PersonByAge_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND ((PersonByName_a1.[SomeName] like @p) AND (PersonByAge_a1.[Age] = @p))
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] WHERE (PersonByName_a1.[SomeName] = @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND (PersonByName_a1.[SomeName] = @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND 1 = 1
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND PersonByName_a1.[SomeName] IN (@p0, @p1, @p2)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByName] AS PersonByName_a1 ON PersonByName_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND PersonByName_a1.[SomeName] NOT IN (@p0, @p1, @p2)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByNullableAge] AS PersonByNullableAge_a1 ON PersonByNullableAge_a1.[DocumentId] = [tpDocument].[Id]
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonByNullableAge] AS PersonByNullableAge_a1 ON PersonByNullableAge_a1.[DocumentId] = [tpDocument].[Id] WHERE (PersonByNullableAge_a1.[Age] IS NULL)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonIdentity] AS PersonIdentity_a1 ON PersonIdentity_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND (PersonIdentity_a1.[Identity] = @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonIdentity] AS PersonIdentity_a2 ON PersonIdentity_a2.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpPersonIdentity] AS PersonIdentity_a3 ON PersonIdentity_a3.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpPersonIdentity] AS PersonIdentity_a4 ON PersonIdentity_a4.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND ( ((PersonIdentity_a2.[Identity] = @p) OR(PersonIdentity_a3.[Identity] = @p)) AND PersonIdentity_a4.[Identity] NOT IN (SELECT PersonIdentity_a4.[Identity] FROM [tpPersonIdentity] AS PersonIdentity_a4 WHERE (PersonIdentity_a4.[Identity] = @p)) )
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonIdentity] AS PersonIdentity_a2 ON PersonIdentity_a2.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpPersonIdentity] AS PersonIdentity_a3 ON PersonIdentity_a3.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND ((PersonIdentity_a2.[Identity] = @p) OR(PersonIdentity_a3.[Identity] = @p))
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPersonIdentity] AS PersonIdentity_a3 ON PersonIdentity_a3.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpPersonIdentity] AS PersonIdentity_a4 ON PersonIdentity_a4.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpPersonIdentity] AS PersonIdentity_a6 ON PersonIdentity_a6.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p AND ( ((PersonIdentity_a3.[Identity] = @p) OR(PersonIdentity_a4.[Identity] = @p)) AND PersonIdentity_a6.[Identity] NOT IN (SELECT PersonIdentity_a6.[Identity] FROM [tpPersonIdentity] AS PersonIdentity_a6 WHERE (PersonIdentity_a6.[Identity] = @p)) )
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPublishedArticle] AS PublishedArticle_a1 ON PublishedArticle_a1.[DocumentId] = [tpDocument].[Id]
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPublishedArticle] AS PublishedArticle_a1 ON PublishedArticle_a1.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticlesByDay_Document] AS ArticlesByDay_Document_a1 ON ArticlesByDay_Document_a1.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpArticlesByDay] AS ArticlesByDay_a1 ON ArticlesByDay_a1.[Id] = ArticlesByDay_Document_a1.[ArticlesByDayId] WHERE [tpDocument].[Type] = @p AND (ArticlesByDay_a1.[DayOfYear] = @p)
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpPublishedArticle] AS PublishedArticle_a1 ON PublishedArticle_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpShapeIndex] AS ShapeIndex_a1 ON ShapeIndex_a1.[DocumentId] = [tpDocument].[Id]
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpShapeIndex] AS ShapeIndex_a1 ON ShapeIndex_a1.[DocumentId] = [tpDocument].[Id] WHERE [tpDocument].[Type] = @p
SELECT count(distinct [tpDocument].[Id]) FROM [tpDocument] INNER JOIN [tpUserByRoleNameIndex_Document] AS UserByRoleNameIndex_Document_a1 ON UserByRoleNameIndex_Document_a1.[DocumentId] = [tpDocument].[Id] INNER JOIN [tpUserByRoleNameIndex] AS UserByRoleNameIndex_a1 ON UserByRoleNameIndex_a1.[Id] = UserByRoleNameIndex_Document_a1.[UserByRoleNameIndexId] WHERE (UserByRoleNameIndex_a1.[RoleName] = @p)
SELECT DateTimePart("day", [PublishedDateTime]) FROM [tpArticleByPublishedDate]
SELECT DateTimePart("hour", [PublishedDateTime]) FROM [tpArticleByPublishedDate]
SELECT DateTimePart("minute", [PublishedDateTime]) FROM [tpArticleByPublishedDate]
SELECT DateTimePart("month", [PublishedDateTime]) FROM [tpArticleByPublishedDate]
SELECT DateTimePart("second", [PublishedDateTime]) FROM [tpArticleByPublishedDate]
SELECT DateTimePart("year", [PublishedDateTime]) FROM [tpArticleByPublishedDate]
SELECT MAX([Id]) FROM [Document]
SELECT MAX([Id]) FROM [OpenId_Document]
SELECT MAX([Id]) FROM [tpCol1_Document]
SELECT MAX([Id]) FROM [tpDocument]
update [Document] set [Content] = @p, [Version] = @p where [Id] = @p ;
update [Document] set [Content] = @p, [Version] = @p where [Id] = @p and [Version] = 1 ;
UPDATE [Document] SET [Content] = REPLACE([Content], 'str', 'str')
UPDATE [Document] SET [Content] = REPLACE([Content], 'str', 'str') WHERE [Type] = 'str'
UPDATE [Document] SET [Content] = REPLACE([Content], @p, 'str')
UPDATE [Document] SET [Content] = REPLACE([Content], NULL, 'str')
update [tpArticlesByDay] set [Count] = @p, [DayOfYear] = @p where [Id] = @p;
update [tpAttachmentByDay] set [Date] = @p, [Count] = @p where [Id] = @p;
update [tpDocument] set [Content] = @p, [Version] = @p where [Id] = @p ;
update [tpDocument] set [Content] = @p, [Version] = @p where [Id] = @p and [Version] = 1 ;
update [tpUserByRoleNameIndex] set [RoleName] = @p, [Count] = @p where [Id] = @p;
