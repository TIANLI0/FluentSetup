using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FluentSetup.Builder.Core.Packaging;

/// <summary>
/// The update feed (update.feed) that <c>fsetup build --feed</c> writes. Its top-level fields describe the stable
/// release: <c>{ "version", "url", "sha256", "size", "notes" }</c>, which every FluentSetup reads. Other channels are
/// entries of the same shape under <c>"channels"</c>, e.g. <c>{ ..., "channels": { "beta": { "version": "2.0.0-beta.1", ... } } }</c>;
/// installers up to 1.5.0 ignore them (src/Bootstrapper/feed.cpp reads both).
/// </summary>
public static partial class UpdateFeed
{
    public const string StableChannel = "stable";

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex ChannelRegex();

    /// <summary>A channel name: lower-case letters, digits and '-', starting with a letter, at most 32 characters.</summary>
    public static bool IsValidChannel(string? channel) => channel is not null && ChannelRegex().IsMatch(channel);

    public sealed record Entry(string Version, string Url, string Sha256, long Size, string? Notes);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The feed with <paramref name="entry"/> as the release of <paramref name="channel"/>: the top-level fields for
    /// "stable", <c>channels.&lt;channel&gt;</c> otherwise. Everything else in <paramref name="existing"/> (other channels,
    /// fields FluentSetup does not know) is kept. <paramref name="existing"/> null or blank: a new feed.
    /// </summary>
    /// <exception cref="InvalidDataException">The existing feed is not a JSON object (or its "channels" is not one).</exception>
    public static string Merge(string? existing, string channel, Entry entry)
    {
        if (!IsValidChannel(channel)) throw new ArgumentException($"'{channel}' is not a channel name.", nameof(channel));
        JsonObject root;
        if (string.IsNullOrWhiteSpace(existing))
        {
            root = [];
        }
        else
        {
            try
            {
                root = JsonNode.Parse(existing, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                       ?? throw new InvalidDataException("The existing feed is not a JSON object.");
            }
            catch (JsonException e)
            {
                throw new InvalidDataException("The existing feed is not valid JSON: " + e.Message, e);
            }
        }
        JsonObject target;
        if (channel == StableChannel)
        {
            target = root;
        }
        else
        {
            if (root["channels"] is null) root["channels"] = new JsonObject();
            if (root["channels"] is not JsonObject channels) throw new InvalidDataException("\"channels\" in the existing feed is not a JSON object.");
            if (channels[channel] is not JsonObject c) channels[channel] = c = [];
            target = c;
        }
        target["version"] = entry.Version;
        target["url"] = entry.Url;
        target["sha256"] = entry.Sha256.ToLowerInvariant();
        target["size"] = entry.Size;
        if (entry.Notes is not null) target["notes"] = entry.Notes;
        else target.Remove("notes");  // the notes of the previous release would be wrong
        return root.ToJsonString(Indented) + Environment.NewLine;
    }
}
