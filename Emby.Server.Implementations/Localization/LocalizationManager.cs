using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Extensions;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Localization
{
    /// <summary>
    /// Class LocalizationManager.
    /// </summary>
    public class LocalizationManager : ILocalizationManager
    {
        private const string DefaultCulture = "en-US";
        private const string RatingsPath = "Emby.Server.Implementations.Localization.Ratings.";
        private const string CulturesPath = "Emby.Server.Implementations.Localization.iso6392.txt";
        private const string CountriesPath = "Emby.Server.Implementations.Localization.countries.json";
        private static readonly Assembly _assembly = typeof(LocalizationManager).Assembly;
        private static readonly string[] _unratedValues = { "n/a", "unrated", "not rated" };

        private readonly IServerConfigurationManager _configurationManager;
        private readonly ILogger<LocalizationManager> _logger;

        private readonly Dictionary<string, Dictionary<string, ParentalRating>> _allParentalRatings =
            new Dictionary<string, Dictionary<string, ParentalRating>>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, Dictionary<string, string>> _dictionaries =
            new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;

        private List<CultureDto> _cultures = new List<CultureDto>();

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalizationManager" /> class.
        /// </summary>
        /// <param name="configurationManager">The configuration manager.</param>
        /// <param name="logger">The logger.</param>
        public LocalizationManager(
            IServerConfigurationManager configurationManager,
            ILogger<LocalizationManager> logger)
        {
            _configurationManager = configurationManager;
            _logger = logger;
        }

        /// <summary>
        /// Loads all resources into memory.
        /// </summary>
        /// <returns><see cref="Task" />.</returns>
        public async Task LoadAll()
        {
            // Extract from the assembly
            foreach (var resource in _assembly.GetManifestResourceNames())
            {
                if (!resource.StartsWith(RatingsPath, StringComparison.Ordinal))
                {
                    continue;
                }

                string countryCode = resource.Substring(RatingsPath.Length, 2);
                var dict = new Dictionary<string, ParentalRating>(StringComparer.OrdinalIgnoreCase);

                await using var stream = _assembly.GetManifestResourceStream(resource);
                using var reader = new StreamReader(stream!); // shouldn't be null here, we just got the resource path from Assembly.GetManifestResourceNames()
                await foreach (var line in reader.ReadAllLinesAsync().ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    string[] parts = line.Split(',');
                    if (parts.Length == 2
                        && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    {
                        var name = parts[0];
                        dict.Add(name, new ParentalRating(name, value));
                    }
#if DEBUG
                    else
                    {
                        _logger.LogWarning("Malformed line in ratings file for country {CountryCode}", countryCode);
                    }
#endif
                }

                _allParentalRatings[countryCode] = dict;
            }

            await LoadCultures().ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the cultures.
        /// </summary>
        /// <returns><see cref="IEnumerable{CultureDto}" />.</returns>
        public IEnumerable<CultureDto> GetCultures()
            => _cultures;

        private async Task LoadCultures()
        {
            List<CultureDto> list = new List<CultureDto>();

            await using var stream = _assembly.GetManifestResourceStream(CulturesPath)
                ?? throw new InvalidOperationException($"Invalid resource path: '{CulturesPath}'");
            using var reader = new StreamReader(stream);
            await foreach (var line in reader.ReadAllLinesAsync().ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var parts = line.Split('|');

                if (parts.Length == 5)
                {
                    string name = parts[3];
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    string twoCharName = parts[2];
                    if (string.IsNullOrWhiteSpace(twoCharName))
                    {
                        continue;
                    }

                    string[] threeletterNames;
                    if (string.IsNullOrWhiteSpace(parts[1]))
                    {
                        threeletterNames = new[] { parts[0] };
                    }
                    else
                    {
                        threeletterNames = new[] { parts[0], parts[1] };
                    }

                    list.Add(new CultureDto
                    {
                        DisplayName = name,
                        Name = name,
                        ThreeLetterISOLanguageNames = threeletterNames,
                        TwoLetterISOLanguageName = twoCharName
                    });
                }
            }

            _cultures = list;
        }

        /// <inheritdoc />
        public CultureDto? FindLanguageInfo(string language)
        {
            // TODO language should ideally be a ReadOnlySpan but moq cannot mock ref structs
            for (var i = 0; i < _cultures.Count; i++)
            {
                var culture = _cultures[i];
                if (language.Equals(culture.DisplayName, StringComparison.OrdinalIgnoreCase)
                    || language.Equals(culture.Name, StringComparison.OrdinalIgnoreCase)
                    || culture.ThreeLetterISOLanguageNames.Contains(language, StringComparison.OrdinalIgnoreCase)
                    || language.Equals(culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
                {
                    return culture;
                }
            }

            return default;
        }

        /// <inheritdoc />
        public IEnumerable<CountryInfo> GetCountries()
        {
            using StreamReader reader = new StreamReader(
                _assembly.GetManifestResourceStream(CountriesPath) ?? throw new InvalidOperationException($"Invalid resource path: '{CountriesPath}'"));
            return JsonSerializer.Deserialize<IEnumerable<CountryInfo>>(reader.ReadToEnd(), _jsonOptions)
                ?? throw new InvalidOperationException($"Resource contains invalid data: '{CountriesPath}'");
        }

        /// <inheritdoc />
        public IEnumerable<ParentalRating> GetParentalRatings()
            => GetParentalRatingsDictionary().Values;

        /// <summary>
        /// Gets the parental ratings dictionary.
        /// </summary>
        /// <returns><see cref="Dictionary{String, ParentalRating}" />.</returns>
        private Dictionary<string, ParentalRating> GetParentalRatingsDictionary()
        {
            var countryCode = _configurationManager.Configuration.MetadataCountryCode;

            if (string.IsNullOrEmpty(countryCode))
            {
                countryCode = "us";
            }

            return GetRatings(countryCode)
                ?? GetRatings("us")
                ?? throw new InvalidOperationException($"Invalid resource path: '{CountriesPath}'");
        }

        /// <summary>
        /// Gets the ratings.
        /// </summary>
        /// <param name="countryCode">The country code.</param>
        /// <returns>The ratings.</returns>
        private Dictionary<string, ParentalRating>? GetRatings(string countryCode)
        {
            _allParentalRatings.TryGetValue(countryCode, out var value);

            return value;
        }

        /// <inheritdoc />
        public int? GetRatingLevel(string rating)
        {
            if (string.IsNullOrEmpty(rating))
            {
                throw new ArgumentNullException(nameof(rating));
            }

            if (_unratedValues.Contains(rating.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Fairly common for some users to have "Rated R" in their rating field
            rating = rating.Replace("Rated ", string.Empty, StringComparison.OrdinalIgnoreCase);

            var ratingsDictionary = GetParentalRatingsDictionary();

            if (ratingsDictionary.TryGetValue(rating, out ParentalRating? value))
            {
                return value.Value;
            }

            // If we don't find anything check all ratings systems
            foreach (var dictionary in _allParentalRatings.Values)
            {
                if (dictionary.TryGetValue(rating, out value))
                {
                    return value.Value;
                }
            }

            // Try splitting by : to handle "Germany: FSK 18"
            var index = rating.IndexOf(':', StringComparison.Ordinal);
            if (index != -1)
            {
                var trimmedRating = rating.AsSpan(index).TrimStart(':').Trim();

                if (!trimmedRating.IsEmpty)
                {
                    return GetRatingLevel(trimmedRating.ToString());
                }
            }

            // TODO: Further improve by normalizing out all spaces and dashes
            return null;
        }

        /// <inheritdoc />
        public string GetLocalizedString(string phrase)
        {
            return GetLocalizedString(phrase, _configurationManager.Configuration.UICulture);
        }

        /// <inheritdoc />
        public string GetLocalizedString(string phrase, string culture)
        {
            if (string.IsNullOrEmpty(culture))
            {
                culture = _configurationManager.Configuration.UICulture;
            }

            if (string.IsNullOrEmpty(culture))
            {
                culture = DefaultCulture;
            }

            var dictionary = GetLocalizationDictionary(culture);

            if (dictionary.TryGetValue(phrase, out var value))
            {
                return value;
            }

            return phrase;
        }

        private Dictionary<string, string> GetLocalizationDictionary(string culture)
        {
            if (string.IsNullOrEmpty(culture))
            {
                throw new ArgumentNullException(nameof(culture));
            }

            const string Prefix = "Core";

            return _dictionaries.GetOrAdd(
                culture,
                (key, localizationManager) => localizationManager.GetDictionary(Prefix, key, DefaultCulture + ".json").GetAwaiter().GetResult(),
                this);
        }

        private async Task<Dictionary<string, string>> GetDictionary(string prefix, string culture, string baseFilename)
        {
            if (string.IsNullOrEmpty(culture))
            {
                throw new ArgumentNullException(nameof(culture));
            }

            var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var namespaceName = GetType().Namespace + "." + prefix;

            await CopyInto(dictionary, namespaceName + "." + baseFilename).ConfigureAwait(false);
            await CopyInto(dictionary, namespaceName + "." + GetResourceFilename(culture)).ConfigureAwait(false);

            return dictionary;
        }

        private async Task CopyInto(IDictionary<string, string> dictionary, string resourcePath)
        {
            await using var stream = _assembly.GetManifestResourceStream(resourcePath);
            // If a Culture doesn't have a translation the stream will be null and it defaults to en-us further up the chain
            if (stream == null)
            {
                _logger.LogError("Missing translation/culture resource: {ResourcePath}", resourcePath);
                return;
            }

            var dict = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, _jsonOptions).ConfigureAwait(false);
            if (dict == null)
            {
                throw new InvalidOperationException($"Resource contains invalid data: '{stream}'");
            }

            foreach (var key in dict.Keys)
            {
                dictionary[key] = dict[key];
            }
        }

        private static string GetResourceFilename(string culture)
        {
            var parts = culture.Split('-');

            if (parts.Length == 2)
            {
                culture = parts[0].ToLowerInvariant() + "-" + parts[1].ToUpperInvariant();
            }
            else
            {
                culture = culture.ToLowerInvariant();
            }

            return culture + ".json";
        }

        /// <inheritdoc />
        public IEnumerable<LocalizationOption> GetLocalizationOptions()
        {
            yield return new LocalizationOption("Afrikaans", "af");
            yield return new LocalizationOption("Arabic", "ar");
            yield return new LocalizationOption("Bulgarian (Bulgaria)", "bg-BG");
            yield return new LocalizationOption("Catalan", "ca");
            yield return new LocalizationOption("Chinese (Hong Kong)", "zh-HK");
            yield return new LocalizationOption("Chinese Simplified", "zh-CN");
            yield return new LocalizationOption("Chinese Traditional", "zh-TW");
            yield return new LocalizationOption("Croatian", "hr");
            yield return new LocalizationOption("Czech", "cs");
            yield return new LocalizationOption("Danish", "da");
            yield return new LocalizationOption("Dutch", "nl");
            yield return new LocalizationOption("English (United Kingdom)", "en-GB");
            yield return new LocalizationOption("English (United States)", "en-US");
            yield return new LocalizationOption("Esperanto", "eo");
            yield return new LocalizationOption("Estonian", "et");
            yield return new LocalizationOption("Finnish", "fi");
            yield return new LocalizationOption("French", "fr");
            yield return new LocalizationOption("French (Canada)", "fr-CA");
            yield return new LocalizationOption("German", "de");
            yield return new LocalizationOption("Greek", "el");
            yield return new LocalizationOption("Hebrew", "he");
            yield return new LocalizationOption("Hungarian", "hu");
            yield return new LocalizationOption("Icelandic", "is");
            yield return new LocalizationOption("Indonesian", "id");
            yield return new LocalizationOption("Italian", "it");
            yield return new LocalizationOption("Japanese", "ja");
            yield return new LocalizationOption("Kazakh", "kk");
            yield return new LocalizationOption("Korean", "ko");
            yield return new LocalizationOption("Latvian", "lv");
            yield return new LocalizationOption("Lithuanian", "lt-LT");
            yield return new LocalizationOption("Malay", "ms");
            yield return new LocalizationOption("Malayalam", "ml");
            yield return new LocalizationOption("Norwegian Bokmål", "nb");
            yield return new LocalizationOption("Norwegian Nynorsk", "nn");
            yield return new LocalizationOption("Persian", "fa");
            yield return new LocalizationOption("Polish", "pl");
            yield return new LocalizationOption("Portuguese", "pt");
            yield return new LocalizationOption("Portuguese (Brazil)", "pt-BR");
            yield return new LocalizationOption("Portuguese (Portugal)", "pt-PT");
            yield return new LocalizationOption("Romanian", "ro");
            yield return new LocalizationOption("Russian", "ru");
            yield return new LocalizationOption("Serbian", "sr");
            yield return new LocalizationOption("Slovak", "sk");
            yield return new LocalizationOption("Slovenian (Slovenia)", "sl-SI");
            yield return new LocalizationOption("Spanish", "es");
            yield return new LocalizationOption("Spanish (Argentina)", "es-AR");
            yield return new LocalizationOption("Spanish (Latin America)", "es-419");
            yield return new LocalizationOption("Spanish (Mexico)", "es-MX");
            yield return new LocalizationOption("Swedish", "sv");
            yield return new LocalizationOption("Swiss German", "gsw");
            yield return new LocalizationOption("Tamil", "ta");
            yield return new LocalizationOption("Telugu", "te");
            yield return new LocalizationOption("Turkish", "tr");
            yield return new LocalizationOption("Tiếng Việt", "vi");
            yield return new LocalizationOption("Ukrainian", "uk");
        }
    }
}
