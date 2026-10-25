// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Globalization;

/// <summary>
/// Normalizes the HTTP and HTTPS locations of CRL distribution points, so equal locations compare equal wherever they
/// are written: in a certificate's distribution points, in a CRL's issuing distribution point, and in the
/// specification's independent procedure.
/// </summary>
/// <remarks>
/// The rule works on the URI as written, without .NET's escaping rules, so other implementations can follow it exactly:
/// the scheme and host are lower-cased, an IPv6 host keeps its brackets, user information, the fragment and a default
/// port are dropped, an empty path becomes <c>/</c>, dot segments are removed (RFC 3986, section 5.2.4), and the path
/// and query are otherwise kept as written.
/// </remarks>
internal static class CrlLocation
{
    /// <summary>
    /// Normalizes a location.
    /// </summary>
    /// <param name="location">The location as written.</param>
    /// <returns>The normalized location, or <c>null</c> when it is not an absolute HTTP or HTTPS URI with a host.</returns>
    public static String? Normalize(String location)
    {
        ArgumentNullException.ThrowIfNull(location);
        var text = location.Split('#', 2)[0];
        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return null;
        }

        var scheme = text[..schemeEnd].ToLowerInvariant();
        var defaultPort = scheme switch
        {
            "http" => 80,
            "https" => 443,
            _ => 0
        };
        if (defaultPort == 0)
        {
            return null;
        }

        var rest = text[(schemeEnd + 3)..];
        var authorityEnd = rest.IndexOfAny(['/', '?']);
        var authority = authorityEnd < 0 ? rest : rest[..authorityEnd];
        var pathAndQuery = authorityEnd < 0 ? String.Empty : rest[authorityEnd..];
        authority = authority[(authority.LastIndexOf('@') + 1)..];

        String host;
        String port;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0 || (close + 1 < authority.Length && authority[close + 1] != ':'))
            {
                return null;
            }

            host = authority[..(close + 1)];
            port = close + 1 < authority.Length ? authority[(close + 2)..] : String.Empty;
        }
        else
        {
            var colon = authority.IndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            port = colon < 0 ? String.Empty : authority[(colon + 1)..];
        }

        if (host.Length == 0)
        {
            return null;
        }

        var portSuffix = String.Empty;
        if (port.Length > 0)
        {
            if (!Int32.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            portSuffix = number == defaultPort ? String.Empty : ":" + number.ToString(CultureInfo.InvariantCulture);
        }

        var queryStart = pathAndQuery.IndexOf('?');
        var path = queryStart < 0 ? pathAndQuery : pathAndQuery[..queryStart];
        var query = queryStart < 0 ? String.Empty : pathAndQuery[queryStart..];
        return $"{scheme}://{host.ToLowerInvariant()}{portSuffix}{RemoveDotSegments(path.Length == 0 ? "/" : path)}{query}";
    }

    /// <summary>
    /// Removes the dot segments of an absolute path as RFC 3986, section 5.2.4, describes.
    /// </summary>
    private static String RemoveDotSegments(String path)
    {
        var output = new List<String>();
        var segments = path.Split('/');
        for (var index = 1; index < segments.Length; index++)
        {
            var segment = segments[index];
            var last = index == segments.Length - 1;
            if (segment == "..")
            {
                if (output.Count > 0)
                {
                    output.RemoveAt(output.Count - 1);
                }

                if (last)
                {
                    output.Add(String.Empty);
                }
            }
            else if (segment == ".")
            {
                if (last)
                {
                    output.Add(String.Empty);
                }
            }
            else
            {
                output.Add(segment);
            }
        }

        return "/" + String.Join('/', output);
    }
}
