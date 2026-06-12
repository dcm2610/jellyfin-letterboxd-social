namespace Jellyfin.Plugin.LetterboxdSocial.Models;

/// <summary>
/// A friend review returned to Jellyfin Web.
/// </summary>
public sealed class LetterboxdReviewResponse
{
    /// <summary>
    /// Gets or sets the mapped Jellyfin user id.
    /// </summary>
    public string JellyfinUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Letterboxd username.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name configured for this user.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Letterboxd profile image URL.
    /// </summary>
    public string AvatarUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the clean star rating string.
    /// </summary>
    public string StarRating { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the numeric star rating, when available.
    /// </summary>
    public double? RatingValue { get; set; }

    /// <summary>
    /// Gets or sets the review text, when available.
    /// </summary>
    public string? ReviewText { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the review is marked as containing spoilers.
    /// </summary>
    public bool ContainsSpoilers { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user has watched this film on Letterboxd.
    /// </summary>
    public bool HasWatched { get; set; } = true;

    /// <summary>
    /// Gets or sets the watched date text, when available.
    /// </summary>
    public string? WatchedDate { get; set; }

    /// <summary>
    /// Gets or sets the Letterboxd film slug.
    /// </summary>
    public string LetterboxdSlug { get; set; } = string.Empty;
}

/// <summary>
/// A scraped Letterboxd film rating before persistence.
/// </summary>
public sealed class ScrapedFilmEntry
{
    /// <summary>
    /// Gets or sets the Letterboxd film slug.
    /// </summary>
    public string LetterboxdSlug { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the rating label.
    /// </summary>
    public string StarRating { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the numeric rating.
    /// </summary>
    public double? RatingValue { get; set; }

    /// <summary>
    /// Gets or sets review text.
    /// </summary>
    public string? ReviewText { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the review is marked as containing spoilers.
    /// </summary>
    public bool ContainsSpoilers { get; set; }

    /// <summary>
    /// Gets or sets the user review page path, when available.
    /// </summary>
    public string? ReviewPath { get; set; }

    /// <summary>
    /// Gets or sets the watched date text when available.
    /// </summary>
    public string? WatchedDate { get; set; }
}

/// <summary>
/// External identifiers resolved from a Letterboxd film page.
/// </summary>
public sealed class FilmExternalIds
{
    /// <summary>
    /// Gets or sets the TMDB movie id.
    /// </summary>
    public string? TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the IMDb title id.
    /// </summary>
    public string? ImdbId { get; set; }
}

/// <summary>
/// A normalized cache record ready to be stored.
/// </summary>
public sealed class CachedReviewRecord
{
    /// <summary>
    /// Gets or sets the TMDB id.
    /// </summary>
    public string? TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the IMDb id.
    /// </summary>
    public string? ImdbId { get; set; }

    /// <summary>
    /// Gets or sets the Letterboxd film slug.
    /// </summary>
    public string LetterboxdSlug { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin user id.
    /// </summary>
    public string JellyfinUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Letterboxd username.
    /// </summary>
    public string LetterboxdUsername { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the configured display name.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Letterboxd profile image URL.
    /// </summary>
    public string AvatarUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the rating label.
    /// </summary>
    public string StarRating { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the numeric rating.
    /// </summary>
    public double? RatingValue { get; set; }

    /// <summary>
    /// Gets or sets the review text.
    /// </summary>
    public string? ReviewText { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the review is marked as containing spoilers.
    /// </summary>
    public bool ContainsSpoilers { get; set; }

    /// <summary>
    /// Gets or sets the watched date text.
    /// </summary>
    public string? WatchedDate { get; set; }
}
