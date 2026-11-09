using bProjects.App.AI;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using bProjects.App.Retrieval;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>Documento sintetico seminato: identificativi e blocchi in ordine di lettura.</summary>
internal sealed record SeededDocument(Guid ProjectId, Guid DocumentId, Guid VersionId, Guid PreparationId, Guid ArtifactId, int Version, string Name, IReadOnlyList<Guid> BlockIds, IReadOnlyList<string> Texts);

/// <summary>Servizi finti del recupero: server di embedding FINTO (nessuna rete, nessun DGX), client, readiness e opzioni.</summary>
internal sealed class RetrievalRig : IDisposable
{
    public RetrievalRig(FakeEmbeddingServer? server = null)
    {
        Server = server ?? new FakeEmbeddingServer();
        Options = new RetrievalOptions { Enabled = true, RuntimeCheckSeconds = 60, Embedding = { Endpoint = "http://dgx.internal:8081/v1/", ApiKey = "k" } };
        Embedding = new EmbeddingClient(Options, Server);
        Readiness = new RetrievalModelReadiness(Options, Embedding, Server);
    }

    public FakeEmbeddingServer Server { get; }

    public RetrievalOptions Options { get; }

    public EmbeddingClient Embedding { get; }

    public RetrievalModelReadiness Readiness { get; }

    /// <summary>Richieste di embedding di CONTENUTO (esclusa la sonda di readiness).</summary>
    public int ContentInputs => Server.EmbeddingInputs.Where(i => !(i.Length == 1 && i[0] == "probe")).Sum(i => i.Length);

    public RetrievalIndexBuilder Builder(AppDbContext db) => new(db, Embedding, Readiness, Options);

    public HybridSearch Search(AppDbContext db) => new(db, Embedding, Readiness);

    public void Dispose()
    {
        Readiness.Dispose();
        Embedding.Dispose();
    }
}

/// <summary>
/// Database PostgreSQL di prova (pgvector, migrazioni complete) con dati documentali sintetici seminati direttamente: la seminagione usa
/// <c>session_replication_role = replica</c> (solo nel test) per non ricostruire l'intera catena di upload/parsing della 002; i vincoli CHECK restano attivi.
/// </summary>
internal sealed class RetrievalDb : IAsyncDisposable
{
    private readonly string _admin;
    private readonly string _name;
    private readonly Guid _user = Guid.NewGuid();
    private bool _userSeeded;

    private RetrievalDb(string admin, string name)
    {
        _admin = admin;
        _name = name;
        ConnectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }

    public string ConnectionString { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<RetrievalDb> CreateAsync()
    {
        var admin = TestEnvironment.RequirePostgres();
        var database = new RetrievalDb(admin, $"bprojects_test_{Guid.NewGuid():N}");
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync(Ct);
            await new NpgsqlCommand($"CREATE DATABASE \"{database._name}\"", connection).ExecuteNonQueryAsync(Ct);
        }

        await using var db = database.NewContext();
        await db.Database.MigrateAsync(Ct);
        return database;
    }

    public AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task<Guid> SeedProjectAsync(string name = "Progetto")
    {
        var project = Guid.NewGuid();
        await using var db = NewContext();
        if (!_userSeeded)
        {
            db.Users.Add(new IdentityUser<Guid> { Id = _user, UserName = "u" + _user.ToString("N"), NormalizedUserName = "U" + _user.ToString("N").ToUpperInvariant(), SecurityStamp = "s", ConcurrencyStamp = "c" });
            await db.SaveChangesAsync(Ct);
            _userSeeded = true;
        }

        await ExecuteAsync($"INSERT INTO \"Projects\" (\"Id\",\"Name\",\"CreatedBy\",\"CreatedAt\") VALUES ('{project}','{name}','{_user}',now())");
        return project;
    }

    /// <summary>Documento con una versione Ready (preparazione corrente) e un blocco per testo, in ordine di lettura.</summary>
    public async Task<SeededDocument> AddDocumentAsync(
        Guid project, string name, string[] texts, int version = 1, string parser = "parser-1", Guid? documentId = null, string[][]? headings = null)
    {
        var document = documentId ?? Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var preparation = Guid.NewGuid();
        var artifact = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var blockIds = texts.Select(_ => Guid.NewGuid()).ToList();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await new NpgsqlCommand("SET session_replication_role = replica", connection).ExecuteNonQueryAsync(Ct);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);

        if (documentId is null)
        {
            db.ProjectDocuments.Add(new ProjectDocument { Id = document, ProjectId = project, DisplayName = name, CreatedBy = _user, CreatedAt = now });
        }

        db.DocumentVersions.Add(new DocumentVersion
        {
            Id = versionId, ProjectId = project, DocumentId = document, Version = version, OriginalName = name, MediaType = "text/plain", ByteLength = 10,
            Sha256 = new string('a', 64), StorageKey = "k/" + Guid.NewGuid().ToString("N"), UploadedBy = _user, UploadedAt = now, UploadReceiptId = Guid.NewGuid(),
        });
        db.DocumentPreparations.Add(new DocumentPreparation
        {
            Id = preparation, ProjectId = project, DocumentVersionId = versionId, Generation = 1, ParserVersion = parser, ConfigHash = new string('c', 64), ManifestHash = new string('d', 64),
            Status = PreparationStatus.Ready, CurrentEvidenceVersionId = Guid.NewGuid(), RequestedAt = now, PolicyHash = new string('e', 64),
            HardDeadlineAt = now.AddHours(1), EffectiveDeadlineAt = now.AddHours(1), CreatedAt = now,
        });
        db.ParsedArtifacts.Add(new ParsedArtifact
        {
            Id = artifact, ProjectId = project, PreparationId = preparation, JobId = Guid.NewGuid(), FencingToken = 1, SchemaVersion = "parsed-1.0.0", StorageKey = "k/" + Guid.NewGuid().ToString("N"),
            Sha256 = new string('a', 64), ByteLength = 10, ParserConfigHash = new string('b', 64), BlockCount = texts.Length,
        });
        for (var i = 0; i < texts.Length; i++)
        {
            db.DocumentBlocks.Add(new DocumentBlock
            {
                Id = blockIds[i], ProjectId = project, ArtifactId = artifact, Ordinal = i, Ref = $"b{i}", Type = BlockType.Paragraph, CanonicalText = texts[i],
                TextHash = DocumentTestFixtures.Sha256(texts[i]),
                HeadingPathJson = System.Text.Json.JsonSerializer.Serialize(headings is not null && i < headings.Length ? headings[i] : Array.Empty<string>()),
            });
        }

        db.EvidenceDocumentStates.Add(new EvidenceDocumentState
        {
            DocumentVersionId = versionId, ProjectId = project, CurrentPreparationId = preparation, CurrentEvidenceVersionId = Guid.NewGuid(), Revision = 0, UpdatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        return new(project, document, versionId, preparation, artifact, version, name, blockIds, texts);
    }

    /// <summary>Evidenze documentali (una versione Evidence) con fonti sui blocchi indicati; ritorna id della versione Evidence e delle evidenze nell'ordine dato.</summary>
    public async Task<(Guid VersionId, IReadOnlyList<Guid> EvidenceIds)> AddEvidenceAsync(SeededDocument document, params (EvidenceKind Kind, string Statement, int[] BlockIndexes)[] evidences)
    {
        var versionId = Guid.NewGuid();
        var ids = new List<Guid>();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await new NpgsqlCommand("SET session_replication_role = replica", connection).ExecuteNonQueryAsync(Ct);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        db.EvidenceExtractionVersions.Add(new EvidenceExtractionVersion
        {
            Id = versionId, ProjectId = document.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = document.PreparationId, ManifestHash = new string('d', 64), Version = 1,
            JobId = Guid.NewGuid(), SchemaVersion = EvidenceExtractionVersion.DocumentSchemaVersion, PromptVersion = "p", ModelId = "m", Parameters = "{}", EvidenceCount = evidences.Length, CreatedAt = DateTimeOffset.UtcNow,
        });
        for (var i = 0; i < evidences.Length; i++)
        {
            var (kind, statement, blocks) = evidences[i];
            var content = new DocumentEvidenceContent
            {
                SchemaVersion = DocumentEvidenceContent.SchemaVersionValue,
                KindRationale = null,
                Structure = new DocumentEvidenceStructure { Subject = null, Action = null, Object = null, Scope = null, Purpose = null, Instrument = null, Channel = null, Recipient = null },
                Qualifiers = [],
                Sources = [.. blocks.Select((b, part) => new PersistedSource
                {
                    SourceId = Guid.NewGuid(), Kind = EvidenceSourceKind.DocumentPassage, Role = EvidenceSourceRole.Primary, CitationGroupId = Guid.NewGuid(), Part = part,
                    MessageId = null, Sequence = null, DocumentId = document.DocumentId, DocumentVersionId = document.VersionId, PreparationId = document.PreparationId,
                    ArtifactId = document.ArtifactId, BlockId = document.BlockIds[b], Start = 0, End = document.Texts[b].Length, Quote = document.Texts[b],
                })],
            };
            var id = Guid.NewGuid();
            ids.Add(id);
            db.Evidences.Add(new EvidenceItem
            {
                Id = id, VersionId = versionId, ProjectId = document.ProjectId, OriginKind = EvidenceOriginKind.Document, PreparationId = document.PreparationId, Ordinal = i + 1,
                Key = "e" + (i + 1), Kind = kind, Polarity = EvidencePolarity.Affirmed, Certainty = EvidenceCertainty.Declared, Statement = statement, Content = DocumentEvidenceCodec.EncodeContent(content),
            });
        }

        await db.SaveChangesAsync(Ct);
        return (versionId, ids);
    }

    /// <summary>Snapshot di fonti: i documenti inclusi (Ready, con la loro preparazione) e quelli esclusi dal PM.</summary>
    public async Task<Guid> AddSnapshotAsync(
        Guid project, IReadOnlyList<SeededDocument> included, IReadOnlyList<SeededDocument>? excluded = null, IReadOnlyDictionary<Guid, Guid>? evidenceVersions = null)
    {
        var snapshot = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await new NpgsqlCommand("SET session_replication_role = replica", connection).ExecuteNonQueryAsync(Ct);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        db.SourceSnapshots.Add(new SourceSnapshot { Id = snapshot, ProjectId = project, SelectionRevision = 0, Hash = new string('f', 64), CreatedAt = DateTimeOffset.UtcNow });
        foreach (var d in included)
        {
            // Ready richiede una versione Evidence: se il test non ne fornisce una, ne esiste una vuota e motivata (esito esplicito «zero evidenze»).
            var evidenceVersion = evidenceVersions?.GetValueOrDefault(d.PreparationId) ?? Guid.NewGuid();
            if (evidenceVersions?.ContainsKey(d.PreparationId) != true)
            {
                db.EvidenceExtractionVersions.Add(new EvidenceExtractionVersion
                {
                    Id = evidenceVersion, ProjectId = project, OriginKind = EvidenceOriginKind.Document, PreparationId = d.PreparationId, ManifestHash = new string('d', 64), Version = 1,
                    JobId = Guid.NewGuid(), SchemaVersion = EvidenceExtractionVersion.DocumentSchemaVersion, PromptVersion = "p", ModelId = "m", Parameters = "{}",
                    EvidenceCount = 0, NoEvidenceReason = "test", CreatedAt = DateTimeOffset.UtcNow,
                });
            }

            db.SourceSnapshotItems.Add(new SourceSnapshotItem
            {
                Id = Guid.NewGuid(), SnapshotId = snapshot, ProjectId = project, DocumentId = d.DocumentId, Included = true, Availability = SourceAvailability.Ready,
                DocumentVersionId = d.VersionId, PreparationId = d.PreparationId, EvidenceVersionId = evidenceVersion,
            });
        }

        foreach (var d in excluded ?? [])
        {
            db.SourceSnapshotItems.Add(new SourceSnapshotItem
            {
                Id = Guid.NewGuid(), SnapshotId = snapshot, ProjectId = project, DocumentId = d.DocumentId, Included = false, Availability = SourceAvailability.Excluded,
            });
        }

        await db.SaveChangesAsync(Ct);
        return snapshot;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await new NpgsqlCommand(sql, connection).ExecuteNonQueryAsync(Ct);
    }

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        var value = await new NpgsqlCommand(sql, connection).ExecuteScalarAsync(Ct);
        return value is DBNull ? null : value;
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_admin);
        await connection.OpenAsync();
        await new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", connection).ExecuteNonQueryAsync();
    }
}
