using Jellyfin.Plugin.LetterboxdSocial.Models;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.LetterboxdSocial.Services;

/// <summary>
/// SQLite-backed cache for scraped Letterboxd reviews.
/// </summary>
public sealed class LetterboxdCacheStore
{
    private readonly LetterboxdSocialFileLogger _logger;
    private readonly SemaphoreSlim _databaseLock = new(1, 1);
    private bool _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="LetterboxdCacheStore"/> class.
    /// </summary>
    /// <param name="logger">File logger.</param>
    public LetterboxdCacheStore(LetterboxdSocialFileLogger logger)
    {
        _logger = logger;

        try
        {
            SQLitePCL.Batteries_V2.Init();
            _logger.Debug("SQLite native provider initialization completed.");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Unable to initialize SQLite native batteries. SQLite may still be available from the host runtime.");
        }
    }

    private string DatabasePath
    {
        get
        {
            var dataFolder = Plugin.Instance?.DataFolderPath
                ?? Path.Combine(AppContext.BaseDirectory, "letterboxd-social");

            Directory.CreateDirectory(dataFolder);
            return Path.Combine(dataFolder, "letterboxd-social.db");
        }
    }

    /// <summary>
    /// Gets cached reviews for any of the supplied lookup ids.
    /// </summary>
    /// <param name="lookupIds">Normalized lookup ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Cached review response rows.</returns>
    public async Task<IReadOnlyList<LetterboxdReviewResponse>> GetReviewsAsync(
        IEnumerable<string> lookupIds,
        CancellationToken cancellationToken)
    {
        var ids = lookupIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(NormalizeLookupId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (ids.Length == 0)
        {
            _logger.Debug("Cache lookup skipped because no lookup ids were supplied.");
            return [];
        }

        _logger.Debug("Reading cached reviews for lookup ids: " + string.Join(", ", ids) + ".");
        var providerIds = ids.Select(StripLookupPrefix).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _databaseLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var lookupParameterNames = ids.Select((_, index) => "$lookup_id" + index).ToArray();
            var providerParameterNames = providerIds.Select((_, index) => "$provider_id" + index).ToArray();
            var sql = $@"
SELECT DISTINCT
    jellyfin_user_id,
    letterboxd_username,
    display_name,
    avatar_url,
    star_rating,
    rating_value,
    review_text,
    contains_spoilers,
    watched_date,
    letterboxd_slug
FROM reviews
WHERE lookup_id IN ({string.Join(",", lookupParameterNames)})
    OR lower(tmdb_id) IN ({string.Join(",", providerParameterNames)})
    OR lower(imdb_id) IN ({string.Join(",", providerParameterNames)})
    OR ('letterboxd:' || lower(letterboxd_slug)) IN ({string.Join(",", lookupParameterNames)})
    OR lower(letterboxd_slug) IN ({string.Join(",", providerParameterNames)})
ORDER BY display_name COLLATE NOCASE, letterboxd_username COLLATE NOCASE;";

            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            for (var index = 0; index < ids.Length; index++)
            {
                command.Parameters.AddWithValue(lookupParameterNames[index], ids[index]);
            }

            for (var index = 0; index < providerIds.Length; index++)
            {
                command.Parameters.AddWithValue(providerParameterNames[index], providerIds[index]);
            }

            var results = new List<LetterboxdReviewResponse>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new LetterboxdReviewResponse
                {
                    JellyfinUserId = reader.GetString(0),
                    Username = reader.GetString(1),
                    DisplayName = reader.GetString(2),
                    AvatarUrl = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    StarRating = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    RatingValue = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    ReviewText = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ContainsSpoilers = !reader.IsDBNull(7) && reader.GetInt32(7) != 0,
                    HasWatched = true,
                    WatchedDate = reader.IsDBNull(8) ? null : reader.GetString(8),
                    LetterboxdSlug = reader.GetString(9)
                });
            }

            _logger.Debug("Cache lookup returned " + results.Count + " review row(s).");
            return results;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to read Letterboxd Social cache.");
            return [];
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    /// <summary>
    /// Saves a user's latest scraped cache records.
    /// </summary>
    /// <param name="letterboxdUsername">Letterboxd username being refreshed.</param>
    /// <param name="records">Records to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SaveUserReviewsAsync(
        string letterboxdUsername,
        IReadOnlyCollection<CachedReviewRecord> records,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(letterboxdUsername))
        {
            _logger.Warning("Skipped saving cache rows because the Letterboxd username was blank.");
            return;
        }

        _logger.Debug("Saving " + records.Count + " cache row(s) for " + letterboxdUsername + ".");
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _databaseLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = (SqliteTransaction)transaction;
                deleteCommand.CommandText = "DELETE FROM reviews WHERE letterboxd_username = $username;";
                deleteCommand.Parameters.AddWithValue("$username", letterboxdUsername);
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertReviewRecordsAsync(connection, (SqliteTransaction)transaction, records, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var distinctFilmCount = records
                .Where(static item => !string.IsNullOrWhiteSpace(item.LetterboxdSlug))
                .Select(static item => item.LetterboxdSlug)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var tmdbFilmCount = records
                .Where(static item => !string.IsNullOrWhiteSpace(item.TmdbId))
                .Select(static item => item.LetterboxdSlug)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var imdbFilmCount = records
                .Where(static item => !string.IsNullOrWhiteSpace(item.ImdbId))
                .Select(static item => item.LetterboxdSlug)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            _logger.Info("Saved " + records.Count + " Letterboxd cache row(s) for " + letterboxdUsername + " across " + distinctFilmCount + " film(s). TMDB ids on " + tmdbFilmCount + " film(s); IMDb ids on " + imdbFilmCount + " film(s).");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save Letterboxd cache rows for " + letterboxdUsername + ".");
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    /// <summary>
    /// Saves refreshed cache records for one user's one Letterboxd film.
    /// </summary>
    /// <param name="letterboxdUsername">Letterboxd username being refreshed.</param>
    /// <param name="letterboxdSlug">Letterboxd film slug being refreshed.</param>
    /// <param name="records">Records to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SaveUserFilmReviewsAsync(
        string letterboxdUsername,
        string letterboxdSlug,
        IReadOnlyCollection<CachedReviewRecord> records,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(letterboxdUsername) || string.IsNullOrWhiteSpace(letterboxdSlug))
        {
            _logger.Warning("Skipped saving movie cache rows because the Letterboxd username or slug was blank.");
            return;
        }

        _logger.Debug("Saving " + records.Count + " movie cache row(s) for " + letterboxdUsername + " and slug " + letterboxdSlug + ".");
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _databaseLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = (SqliteTransaction)transaction;
                deleteCommand.CommandText = "DELETE FROM reviews WHERE letterboxd_username = $username AND letterboxd_slug = $slug;";
                deleteCommand.Parameters.AddWithValue("$username", letterboxdUsername);
                deleteCommand.Parameters.AddWithValue("$slug", letterboxdSlug);
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertReviewRecordsAsync(connection, (SqliteTransaction)transaction, records, cancellationToken).ConfigureAwait(false);
            await UpsertUserFilmCheckAsync(
                connection,
                (SqliteTransaction)transaction,
                letterboxdUsername,
                letterboxdSlug,
                records.Count > 0,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _logger.Info("Saved " + records.Count + " on-demand Letterboxd cache row(s) for " + letterboxdUsername + " and " + letterboxdSlug + ".");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save movie cache rows for " + letterboxdUsername + " and " + letterboxdSlug + ".");
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    /// <summary>
    /// Marks a direct per-user film page as checked, including misses.
    /// </summary>
    /// <param name="letterboxdUsername">Letterboxd username.</param>
    /// <param name="letterboxdSlug">Letterboxd film slug.</param>
    /// <param name="hasRecord">Whether a watched/rated/reviewed record was found.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SaveUserFilmCheckAsync(
        string letterboxdUsername,
        string letterboxdSlug,
        bool hasRecord,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(letterboxdUsername) || string.IsNullOrWhiteSpace(letterboxdSlug))
        {
            return;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _databaseLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await UpsertUserFilmCheckAsync(
                connection,
                (SqliteTransaction)transaction,
                letterboxdUsername,
                letterboxdSlug,
                hasRecord,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to save direct film check for " + letterboxdUsername + " and " + letterboxdSlug + ".");
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    /// <summary>
    /// Gets users whose direct film page has already been checked.
    /// </summary>
    /// <param name="letterboxdSlug">Letterboxd film slug.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Checked usernames.</returns>
    public async Task<IReadOnlySet<string>> GetCheckedUsernamesForFilmAsync(
        string letterboxdSlug,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(letterboxdSlug))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _databaseLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT letterboxd_username FROM user_film_checks WHERE letterboxd_slug = $slug;";
            command.Parameters.AddWithValue("$slug", letterboxdSlug.Trim());

            var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                usernames.Add(reader.GetString(0));
            }

            return usernames;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to read direct film checks for " + letterboxdSlug + ".");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    /// <summary>
    /// Gets cached external ids for a Letterboxd film slug.
    /// </summary>
    /// <param name="slug">Letterboxd film slug.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>External ids, when cached.</returns>
    public async Task<FilmExternalIds?> GetFilmExternalIdsAsync(string slug, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            _logger.Warning("Skipped external-id cache lookup because the Letterboxd slug was blank.");
            return null;
        }

        _logger.Debug("Reading cached external ids for Letterboxd slug " + slug + ".");
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _databaseLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT tmdb_id, imdb_id FROM film_ids WHERE letterboxd_slug = $slug;";
            command.Parameters.AddWithValue("$slug", slug);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger.Debug("No cached external ids found for Letterboxd slug " + slug + ".");
                return null;
            }

            var ids = new FilmExternalIds
            {
                TmdbId = reader.IsDBNull(0) ? null : reader.GetString(0),
                ImdbId = reader.IsDBNull(1) ? null : reader.GetString(1)
            };

            _logger.Debug("Cached external ids for " + slug + ": TMDB=" + (ids.TmdbId ?? "(none)") + ", IMDb=" + (ids.ImdbId ?? "(none)") + ".");
            return ids;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to read cached external ids for Letterboxd slug " + slug + ".");
            return null;
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    /// <summary>
    /// Saves external ids for a Letterboxd film slug.
    /// </summary>
    /// <param name="slug">Letterboxd film slug.</param>
    /// <param name="externalIds">External ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SaveFilmExternalIdsAsync(string slug, FilmExternalIds externalIds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            _logger.Warning("Skipped saving external ids because the Letterboxd slug was blank.");
            return;
        }

        _logger.Debug("Saving external ids for " + slug + ": TMDB=" + (externalIds.TmdbId ?? "(none)") + ", IMDb=" + (externalIds.ImdbId ?? "(none)") + ".");
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _databaseLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO film_ids (letterboxd_slug, tmdb_id, imdb_id, updated_at)
VALUES ($slug, $tmdb_id, $imdb_id, $updated_at)
ON CONFLICT(letterboxd_slug) DO UPDATE SET
    tmdb_id = excluded.tmdb_id,
    imdb_id = excluded.imdb_id,
    updated_at = excluded.updated_at;";
            command.Parameters.AddWithValue("$slug", slug);
            command.Parameters.AddWithValue("$tmdb_id", ToDbValue(externalIds.TmdbId));
            command.Parameters.AddWithValue("$imdb_id", ToDbValue(externalIds.ImdbId));
            command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to save external ids for Letterboxd slug " + slug + ".");
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    /// <summary>
    /// Normalizes a lookup id for matching.
    /// </summary>
    /// <param name="lookupId">Raw lookup id.</param>
    /// <returns>Normalized lookup id.</returns>
    public static string NormalizeLookupId(string lookupId)
    {
        return lookupId.Trim().ToLowerInvariant();
    }

    private static string StripLookupPrefix(string lookupId)
    {
        var normalized = NormalizeLookupId(lookupId);
        var separatorIndex = normalized.IndexOf(':', StringComparison.Ordinal);
        return separatorIndex >= 0 ? normalized[(separatorIndex + 1)..] : normalized;
    }

    private static object ToDbValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        return new SqliteConnection(builder.ToString());
    }

    private static async Task InsertReviewRecordsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<CachedReviewRecord> records,
        CancellationToken cancellationToken)
    {
        foreach (var record in records.Where(static item => !string.IsNullOrWhiteSpace(item.LookupId)))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO reviews (
    lookup_id,
    tmdb_id,
    imdb_id,
    letterboxd_slug,
    jellyfin_user_id,
    letterboxd_username,
    display_name,
    avatar_url,
    star_rating,
    rating_value,
    review_text,
    contains_spoilers,
    watched_date,
    scraped_at
) VALUES (
    $lookup_id,
    $tmdb_id,
    $imdb_id,
    $letterboxd_slug,
    $jellyfin_user_id,
    $letterboxd_username,
    $display_name,
    $avatar_url,
    $star_rating,
    $rating_value,
    $review_text,
    $contains_spoilers,
    $watched_date,
    $scraped_at
);";

            command.Parameters.AddWithValue("$lookup_id", NormalizeLookupId(record.LookupId));
            command.Parameters.AddWithValue("$tmdb_id", ToDbValue(record.TmdbId));
            command.Parameters.AddWithValue("$imdb_id", ToDbValue(record.ImdbId));
            command.Parameters.AddWithValue("$letterboxd_slug", record.LetterboxdSlug);
            command.Parameters.AddWithValue("$jellyfin_user_id", record.JellyfinUserId);
            command.Parameters.AddWithValue("$letterboxd_username", record.LetterboxdUsername);
            command.Parameters.AddWithValue("$display_name", record.DisplayName);
            command.Parameters.AddWithValue("$avatar_url", ToDbValue(record.AvatarUrl));
            command.Parameters.AddWithValue("$star_rating", ToDbValue(record.StarRating));
            command.Parameters.AddWithValue("$rating_value", record.RatingValue.HasValue ? record.RatingValue.Value : DBNull.Value);
            command.Parameters.AddWithValue("$review_text", ToDbValue(record.ReviewText));
            command.Parameters.AddWithValue("$contains_spoilers", record.ContainsSpoilers ? 1 : 0);
            command.Parameters.AddWithValue("$watched_date", ToDbValue(record.WatchedDate));
            command.Parameters.AddWithValue("$scraped_at", DateTimeOffset.UtcNow.ToString("O"));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UpsertUserFilmCheckAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string letterboxdUsername,
        string letterboxdSlug,
        bool hasRecord,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO user_film_checks (letterboxd_username, letterboxd_slug, has_record, checked_at)
VALUES ($username, $slug, $has_record, $checked_at)
ON CONFLICT(letterboxd_username, letterboxd_slug) DO UPDATE SET
    has_record = excluded.has_record,
    checked_at = excluded.checked_at;";
        command.Parameters.AddWithValue("$username", letterboxdUsername.Trim());
        command.Parameters.AddWithValue("$slug", letterboxdSlug.Trim());
        command.Parameters.AddWithValue("$has_record", hasRecord ? 1 : 0);
        command.Parameters.AddWithValue("$checked_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _databaseLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            foreach (var statement in CreateSchemaStatements())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await EnsureColumnAsync(
                connection,
                "reviews",
                "contains_spoilers",
                "INTEGER NOT NULL DEFAULT 0",
                cancellationToken).ConfigureAwait(false);

            await EnsureColumnAsync(
                connection,
                "reviews",
                "avatar_url",
                "TEXT NULL",
                cancellationToken).ConfigureAwait(false);

            _initialized = true;
            _logger.Info("Letterboxd Social SQLite cache initialized at " + DatabasePath + ".");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialize Letterboxd Social SQLite cache.");
            throw;
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    private static IReadOnlyList<string> CreateSchemaStatements()
    {
        return
        [
            @"
CREATE TABLE IF NOT EXISTS reviews (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    lookup_id TEXT NOT NULL,
    tmdb_id TEXT NULL,
    imdb_id TEXT NULL,
    letterboxd_slug TEXT NOT NULL,
    jellyfin_user_id TEXT NOT NULL,
    letterboxd_username TEXT NOT NULL,
    display_name TEXT NOT NULL,
    avatar_url TEXT NULL,
    star_rating TEXT NULL,
    rating_value REAL NULL,
    review_text TEXT NULL,
    contains_spoilers INTEGER NOT NULL DEFAULT 0,
    watched_date TEXT NULL,
    scraped_at TEXT NOT NULL
);",
            "CREATE INDEX IF NOT EXISTS ix_reviews_lookup_id ON reviews (lookup_id);",
            "CREATE INDEX IF NOT EXISTS ix_reviews_tmdb_id ON reviews (tmdb_id);",
            "CREATE INDEX IF NOT EXISTS ix_reviews_imdb_id ON reviews (imdb_id);",
            "CREATE INDEX IF NOT EXISTS ix_reviews_letterboxd_slug ON reviews (letterboxd_slug);",
            "CREATE INDEX IF NOT EXISTS ix_reviews_letterboxd_username ON reviews (letterboxd_username);",
            @"
CREATE TABLE IF NOT EXISTS film_ids (
    letterboxd_slug TEXT PRIMARY KEY,
    tmdb_id TEXT NULL,
    imdb_id TEXT NULL,
    updated_at TEXT NOT NULL
);",
            @"
CREATE TABLE IF NOT EXISTS user_film_checks (
    letterboxd_username TEXT NOT NULL,
    letterboxd_slug TEXT NOT NULL,
    has_record INTEGER NOT NULL DEFAULT 0,
    checked_at TEXT NOT NULL,
    PRIMARY KEY (letterboxd_username, letterboxd_slug)
);"
        ];
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(" + tableName + ");";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        await using (var alterCommand = connection.CreateCommand())
        {
            alterCommand.CommandText = "ALTER TABLE " + tableName + " ADD COLUMN " + columnName + " " + columnDefinition + ";";
            await alterCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
