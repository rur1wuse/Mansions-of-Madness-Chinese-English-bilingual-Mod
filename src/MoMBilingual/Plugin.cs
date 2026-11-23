using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using AssetBundles;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace MansionsBilingual
{
    [BepInPlugin(Guid, "Mansions of Madness Bilingual Text", "1.1.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "local.mansions.madness.bilingual";
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> OnlyChinese;
        internal static ConfigEntry<string> EnglishColor;
        internal static ConfigEntry<string> InlineSeparator;
        internal static ConfigEntry<int> InlineMaxLength;
        internal static ConfigEntry<int> ParagraphWidthPercent;
        internal static ConfigEntry<bool> DumpLocalization;

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true,
                "在中文文本后面追加英文原文。");
            OnlyChinese = Config.Bind("General", "OnlyChinese", true,
                "只在游戏语言为中文时追加英文。");
            EnglishColor = Config.Bind("Appearance", "EnglishColor", "9A9A9A",
                "英文使用的 NGUI 颜色标签（RRGGBB），留空表示与中文同色。");
            InlineSeparator = Config.Bind("Appearance", "InlineSeparator", " / ",
                "短文本在同一行显示时使用的分隔符。");
            InlineMaxLength = Config.Bind("Appearance", "InlineMaxLength", 8,
                "中文字数不超过该值且不含换行时，英文显示在同一行；更长则换行。");
            ParagraphWidthPercent = Config.Bind("Appearance", "ParagraphWidthPercent", 88,
                "自动换行段落标签的宽度百分比，用于给更长的英文留出左右边距。");
            DumpLocalization = Config.Bind("Debug", "DumpLocalization", false,
                "将合并后的本地化表导出到 BepInEx 目录，供排查使用。");

            try
            {
                new Harmony(Guid).PatchAll(typeof(GetPatch).Assembly);
                Log.LogInfo("Bilingual localization patch applied.");
            }
            catch (Exception ex)
            {
                Log.LogError("Failed to patch localization: " + ex);
            }
        }

        internal static IEnumerator PreloadEnglishScenarioLocalization()
        {
            if (!Enabled.Value)
                yield break;

            // MoM_AssetLoader initializes in PersistantGameObject.CoroutineStart.
            float start = Time.realtimeSinceStartup;
            while (!MoM_AssetLoader.IsInitialized)
            {
                if (Time.realtimeSinceStartup - start > 60f)
                {
                    Log.LogWarning("Asset loader did not initialize; skipping English preload.");
                    yield break;
                }
                yield return null;
            }

            string[] bundles = MoM_AssetLoader.GetAllLocalizationAssetBundleNames();
            if (bundles == null || bundles.Length == 0)
            {
                Log.LogWarning("No localization asset bundles found; skipping English preload.");
                yield break;
            }

            int cachedTables = 0;
            foreach (string bundleName in bundles)
            {
                // The main English table is already loaded by the game.
                if (bundleName == "localization/main")
                    continue;

                string error;
                if (!AssetBundleManager.LoadAssetBundle(bundleName, false, out error))
                {
                    Log.LogWarning("English preload: cannot load " + bundleName + ": " + error);
                    continue;
                }

                try
                {
                    float waitStart = Time.realtimeSinceStartup;
                    while (AssetBundleManager.IsLoadingAssetBundle(bundleName))
                    {
                        if (Time.realtimeSinceStartup - waitStart > 30f)
                            break;
                        yield return null;
                    }

                    LoadedAssetBundle loaded = AssetBundleManager.GetLoadedAssetBundle(bundleName, out error);
                    if (loaded == null)
                    {
                        Log.LogWarning("English preload: " + bundleName + " not available: " + error);
                        continue;
                    }

                    AssetBundle bundle = loaded.m_AssetBundle;
                    if (bundle == null)
                    {
                        Log.LogWarning("English preload: " + bundleName + " has no AssetBundle.");
                        continue;
                    }

                    string[] assetNames = null;
                    try { assetNames = bundle.GetAllAssetNames(); }
                    catch (Exception ex) { Log.LogWarning("English preload: GetAllAssetNames failed: " + ex.Message); }

                    bool cachedOne = false;
                    if (assetNames != null)
                    {
                        foreach (string assetPath in assetNames)
                        {
                            if (!assetPath.EndsWith("_en.bytes", StringComparison.OrdinalIgnoreCase))
                                continue;
                            TextAsset text = bundle.LoadAsset<TextAsset>(assetPath);
                            if (text == null || !text.name.EndsWith("_en", StringComparison.OrdinalIgnoreCase))
                                continue;
                            Dictionary<string, string> table = Bilingual.ParseEnglishCsv(text);
                            if (table != null && table.Count > 0)
                            {
                                string scenarioName = text.name.Substring(0, text.name.Length - 3);
                                Bilingual.CacheScenarioEnglish(scenarioName, table);
                                cachedOne = true;
                                cachedTables++;
                            }
                        }
                    }

                    // Fallback for bundles whose asset names are not exposed.
                    if (!cachedOne)
                    {
                        foreach (TextAsset text in bundle.LoadAllAssets<TextAsset>())
                        {
                            if (text == null || !text.name.EndsWith("_en", StringComparison.OrdinalIgnoreCase))
                                continue;
                            Dictionary<string, string> table = Bilingual.ParseEnglishCsv(text);
                            if (table != null && table.Count > 0)
                            {
                                string scenarioName = text.name.Substring(0, text.name.Length - 3);
                                Bilingual.CacheScenarioEnglish(scenarioName, table);
                                cachedTables++;
                            }
                        }
                    }
                }
                finally
                {
                    AssetBundleManager.UnloadAssetBundle(bundleName);
                }

                yield return null;
            }

            Log.LogInfo("Cached " + cachedTables + " English scenario table(s).");
        }
    }

    internal static class Bilingual
    {
        private static readonly Regex Placeholder = new Regex(@"\{(-?\d+)\}", RegexOptions.Compiled);
        private static readonly HashSet<string> DecoratedTexts = new HashSet<string>();
        private static readonly Dictionary<int, int> AdjustedWidths = new Dictionary<int, int>();

        [ThreadStatic] internal static int SuppressDepth;
        private static readonly Dictionary<string, Dictionary<string, string>> ScenarioEnglish =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        internal static bool PreloadStarted;
        internal static void StartPreload()
        {
            if (PreloadStarted || !Plugin.Enabled.Value)
                return;
            PreloadStarted = true;
            try
            {
                var go = new GameObject("MoMBilingualPreload");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<PreloadRunner>();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("English preload start failed: " + ex.Message);
            }
        }


        internal static bool ShouldDecorate(string key, string localized, out string english)
        {
            english = null;
            if (!Plugin.Enabled.Value || string.IsNullOrEmpty(localized) || SuppressDepth > 0)
                return false;
            if (!Localization.localizationHasBeenSet)
                return false;
            if (Plugin.OnlyChinese.Value && Localization.language != "Chinese")
                return false;

            Dictionary<string, string[]> dict = Localization.dictionary;
            string[] row;
            if (dict == null || !dict.TryGetValue(key, out row) || row == null || row.Length == 0)
                return false;
            int englishIndex = LanguageIndex("English");
            english = englishIndex >= 0 && englishIndex < row.Length ? row[englishIndex] : null;
            if (string.IsNullOrEmpty(english))
                return false;
            if (string.Equals(english, localized, StringComparison.Ordinal))
                return false;
            // Dynamic inserts are resolved by MoM_LocalizationPacket with the same
            // argument list for both languages; appending English before formatting
            // can duplicate those inserts, so leave placeholder messages untouched.
            if (Placeholder.IsMatch(localized) || Placeholder.IsMatch(english))
                return false;
            // Some combined popup rows carry Chinese in the English column; do not
            // append a second copy of the Chinese text.
            if (HasCjk(english))
                return false;
            // A raw key is not a localized string.
            if (string.Equals(localized, key, StringComparison.Ordinal))
                return false;
            return true;
        }

        internal static string Decorate(string localized, string english)
        {
            // English must not introduce placeholders the Chinese text does not have,
            // otherwise MoM_LocalizationPacket's argument counting changes behaviour.
            HashSet<int> zh = Indexes(localized);
            english = RemoveExtraPlaceholders(english, zh);
            if (string.IsNullOrEmpty(english))
                return localized;

            bool inline = localized.IndexOf('\n') < 0 &&
                          localized.Length <= Math.Max(0, Plugin.InlineMaxLength.Value);
            string separator = inline ? Plugin.InlineSeparator.Value : "\n";
            // BepInEx trims the whitespace of the default " / " when it writes
            // the config file; restore the readable spacing for the bare slash.
            if (inline && separator == "/")
                separator = " / ";
            string colored = english;
            string color = Plugin.EnglishColor.Value;
            if (!string.IsNullOrEmpty(color))
                colored = "[" + color + "]" + english + "[-]";

            string tail = separator + colored;
            if (localized.EndsWith(tail, StringComparison.Ordinal))
                return localized;

            string result = localized + tail;
            if (DecoratedTexts.Count > 60000)
                DecoratedTexts.Clear();
            DecoratedTexts.Add(result);
            return result;
        }

        internal static void CacheScenarioEnglish(string scenarioName, Dictionary<string, string> table)
        {
            if (string.IsNullOrEmpty(scenarioName) || table == null || table.Count == 0)
                return;
            ScenarioEnglish[scenarioName] = table;
        }

        internal static bool MergeScenarioEnglish(string scenarioName)
        {
            Dictionary<string, string> table;
            if (string.IsNullOrEmpty(scenarioName) || !ScenarioEnglish.TryGetValue(scenarioName, out table))
                return false;

            Dictionary<string, string[]> dict = Localization.dictionary;
            string[] languages = Localization.knownLanguages;
            int enIndex = LanguageIndex("English");
            if (dict == null || languages == null || enIndex < 0)
                return false;

            foreach (KeyValuePair<string, string> pair in table)
            {
                string[] row;
                if (!dict.TryGetValue(pair.Key, out row) || row == null || row.Length != languages.Length)
                {
                    row = new string[languages.Length];
                    dict[pair.Key] = row;
                }
                row[enIndex] = pair.Value;
            }
            return true;
        }

        internal static Dictionary<string, string> ParseEnglishCsv(TextAsset text)
        {
            try
            {
                var reader = new ByteReader(text);
                var header = reader.ReadCSV();
                if (header == null || header.size < 2)
                    return null;

                var table = new Dictionary<string, string>(StringComparer.Ordinal);
                while (true)
                {
                    var row = reader.ReadCSV();
                    if (row == null || row.size == 0)
                        break;
                    if (row.buffer[0] == null)
                        continue;
                    string key = row.buffer[0];
                    if (key.Length == 0 || key.StartsWith("//", StringComparison.Ordinal))
                        continue;
                    table[key] = row.size > 1 && row.buffer[1] != null ? row.buffer[1] : string.Empty;
                }
                return table;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("English scenario CSV parse failed: " + ex.Message);
                return null;
            }
        }

        internal static string WithEnglish(string key, string chineseFormatted)
        {
            if (!Plugin.Enabled.Value || string.IsNullOrEmpty(chineseFormatted) || SuppressDepth > 0)
                return null;
            if (Plugin.OnlyChinese.Value && Localization.language != "Chinese")
                return null;

            Dictionary<string, string[]> dict = Localization.dictionary;
            string[] row;
            if (dict == null || !dict.TryGetValue(key, out row) || row == null)
                return null;

            int zhIndex = LanguageIndex("Chinese");
            int enIndex = LanguageIndex("English");
            if (zhIndex < 0 || zhIndex >= row.Length)
                return null;

            string zhTemplate = row[zhIndex];
            if (string.IsNullOrEmpty(zhTemplate))
                return null;

            // Prefer the cache for the current scenario; fall back to the global table.
            string enTemplate = null;
            string scenarioKey = null;
            try
            {
                scenarioKey = GameData.Scenario != null && GameData.Scenario.Name != null
                    ? GameData.Scenario.Name.Key
                    : null;
            }
            catch { }
            Dictionary<string, string> scenarioTable;
            if (!string.IsNullOrEmpty(scenarioKey) && ScenarioEnglish.TryGetValue(scenarioKey, out scenarioTable))
                scenarioTable.TryGetValue(key, out enTemplate);

            if (string.IsNullOrEmpty(enTemplate) && enIndex >= 0 && enIndex < row.Length)
                enTemplate = row[enIndex];

            if (string.IsNullOrEmpty(enTemplate))
                return null;
            if (string.Equals(zhTemplate, enTemplate, StringComparison.Ordinal))
                return null;
            if (HasCjk(enTemplate))
                return null;

            HashSet<int> zhIndexes = Indexes(zhTemplate);
            HashSet<int> enIndexes = Indexes(enTemplate);

            if (zhIndexes.Count == 0)
            {
                if (enIndexes.Count > 0)
                    return null;
                return Decorate(chineseFormatted, enTemplate);
            }

            enTemplate = RemoveExtraPlaceholders(enTemplate, zhIndexes);
            if (string.IsNullOrEmpty(enTemplate))
                return null;

            string[] args = ExtractArgs(zhTemplate, chineseFormatted);
            if (args == null)
                return null;
            TranslateArgs(args, scenarioKey);

            string englishFormatted;
            try { englishFormatted = string.Format(enTemplate, args); }
            catch (FormatException) { return null; }
            if (string.IsNullOrEmpty(englishFormatted))
                return null;

            return Decorate(chineseFormatted, englishFormatted);
        }

        private static string RemoveExtraPlaceholders(string english, HashSet<int> zhIndexes)
        {
            if (!Placeholder.IsMatch(english))
                return english;
            bool removed = false;
            string cleaned = Placeholder.Replace(english, m =>
            {
                int index;
                if (int.TryParse(m.Groups[1].Value, out index) && zhIndexes.Contains(index))
                    return m.Value;
                removed = true;
                return string.Empty;
            });
            return removed ? cleaned.Trim() : english;
        }

        private static string[] ExtractArgs(string zhTemplate, string chineseFormatted)
        {
            MatchCollection matches = Placeholder.Matches(zhTemplate);
            if (matches.Count == 0)
                return Array.Empty<string>();

            // Literal segments around each placeholder.
            var literals = new List<string>(matches.Count + 1);
            int last = 0;
            foreach (Match m in matches)
            {
                literals.Add(zhTemplate.Substring(last, m.Index - last));
                last = m.Index + m.Length;
            }
            literals.Add(zhTemplate.Substring(last));

            int maxIndex = -1;
            foreach (Match m in matches)
            {
                int index;
                if (!int.TryParse(m.Groups[1].Value, out index) || index < 0)
                    return null;
                if (index > maxIndex)
                    maxIndex = index;
            }

            var args = new string[maxIndex + 1];
            int cursor = 0;

            // Honor a literal prefix before the first placeholder.
            if (literals[0].Length > 0)
            {
                int prefix = chineseFormatted.IndexOf(literals[0], StringComparison.Ordinal);
                if (prefix < 0)
                    return null;
                cursor = prefix + literals[0].Length;
            }

            for (int i = 0; i < matches.Count; i++)
            {
                string nextLiteral = literals[i + 1];
                int index = int.Parse(matches[i].Groups[1].Value);

                if (nextLiteral.Length == 0 && i == matches.Count - 1)
                {
                    // Trailing placeholder: the rest of the formatted text is the value.
                    if (args[index] == null)
                        args[index] = chineseFormatted.Substring(cursor);
                    cursor = chineseFormatted.Length;
                    continue;
                }

                if (nextLiteral.Length == 0)
                    return null; // adjacent placeholders cannot be delimited reliably

                int pos = chineseFormatted.IndexOf(nextLiteral, cursor, StringComparison.Ordinal);
                if (pos < 0)
                    return null;

                if (args[index] == null)
                    args[index] = chineseFormatted.Substring(cursor, pos - cursor);
                cursor = pos + nextLiteral.Length;
            }

            for (int i = 0; i < args.Length; i++)
                if (args[i] == null)
                    args[i] = string.Empty;

            return args;
        }

        private static void TranslateArgs(string[] args, string scenarioKey)
        {
            Dictionary<string, string[]> dict = Localization.dictionary;
            string[] languages = Localization.knownLanguages;
            int zhIndex = LanguageIndex("Chinese");
            int enIndex = LanguageIndex("English");
            if (dict == null || languages == null || zhIndex < 0 || enIndex < 0)
                return;

            Dictionary<string, string> cache = null;
            if (!string.IsNullOrEmpty(scenarioKey))
                ScenarioEnglish.TryGetValue(scenarioKey, out cache);

            for (int i = 0; i < args.Length; i++)
            {
                string value = args[i];
                if (string.IsNullOrEmpty(value))
                    continue;

                foreach (KeyValuePair<string, string[]> pair in dict)
                {
                    string[] row = pair.Value;
                    if (row == null || zhIndex >= row.Length || row[zhIndex] != value)
                        continue;

                    string english = null;
                    if (cache != null)
                        cache.TryGetValue(pair.Key, out english);
                    if (string.IsNullOrEmpty(english) && enIndex < row.Length)
                        english = row[enIndex];
                    if (!string.IsNullOrEmpty(english) && !HasCjk(english))
                    {
                        args[i] = english;
                        break;
                    }
                }
            }
        }

        private static bool HasCjk(string text)
        {
            foreach (char c in text)
                if ((c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF))
                    return true;
            return false;
        }

        private static int LanguageIndex(string language)
        {
            string[] languages = Localization.knownLanguages;
            if (languages == null)
                return -1;
            for (int i = 0; i < languages.Length; i++)
                if (string.Equals(languages[i], language, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        internal static void InspectLabel(UILabel label)
        {
            if (label == null || string.IsNullOrEmpty(label.text) || !DecoratedTexts.Contains(label.text))
                return;

            bool dirty = false;
            int id = label.GetInstanceID();

            if (label.text.IndexOf('\n') >= 0 && label.spacingY < 2)
            {
                label.spacingY = 2;
                dirty = true;
            }

            // Fixed-size labels (buttons, item names) would cut the second line off.
            if (label.overflowMethod == UILabel.Overflow.ClampContent)
            {
                label.overflowMethod = UILabel.Overflow.ShrinkContent;
                dirty = true;
            }

            if (label.overflowMethod == UILabel.Overflow.ResizeHeight)
            {
                // Let the bilingual paragraph use as many lines as it needs.
                if (label.maxLineCount > 0)
                {
                    label.maxLineCount = 0;
                    dirty = true;
                }

                // Narrow wide paragraphs so the longer English keeps the Chinese margins.
                if (label.width >= 300 && label.width <= 600)
                {
                    int target;
                    if (!AdjustedWidths.TryGetValue(id, out target))
                    {
                        target = Mathf.RoundToInt(label.width *
                            Mathf.Clamp(Plugin.ParagraphWidthPercent.Value, 50, 100) / 100f);
                        AdjustedWidths[id] = target;
                    }
                    if (label.width != target)
                    {
                        label.width = target;
                        dirty = true;
                    }
                }
            }

            if (dirty)
                label.ProcessText();
        }

        internal static void Dump(string name)
        {
            try
            {
                string[] languages = Localization.knownLanguages;
                Dictionary<string, string[]> dict = Localization.dictionary;
                if (languages == null || dict == null)
                    return;
                string path = Path.Combine(Paths.BepInExRootPath, "MoMBilingual_dump_" + name + ".tsv");
                using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
                {
                    writer.Write("KEY");
                    for (int i = 0; i < languages.Length; i++)
                        writer.Write("\t[" + i + "]" + languages[i]);
                    writer.Write("\n");
                    foreach (KeyValuePair<string, string[]> pair in dict)
                    {
                        writer.Write(pair.Key);
                        string[] row = pair.Value;
                        for (int i = 0; i < languages.Length; i++)
                        {
                            writer.Write("\t");
                            if (row != null && i < row.Length && row[i] != null)
                                writer.Write(row[i].Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t"));
                        }
                        writer.Write("\n");
                    }
                }
                Plugin.Log.LogInfo("Dumped localization to " + path);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Dump failed: " + ex);
            }
        }

        private static HashSet<int> Indexes(string text)
        {
            var set = new HashSet<int>();
            foreach (Match match in Placeholder.Matches(text))
            {
                int index;
                if (int.TryParse(match.Groups[1].Value, out index))
                    set.Add(index);
            }
            return set;
        }
    }

    [HarmonyPatch(typeof(Localization), "Get", new Type[] { typeof(string), typeof(bool), typeof(ulong) })]
    internal static class GetPatch
    {
        private static void Postfix(string key, ref string __result)
        {
            Bilingual.StartPreload();
            string english;
            if (!Bilingual.ShouldDecorate(key, __result, out english))
                return;
            __result = Bilingual.Decorate(__result, english);
        }
    }

    [HarmonyPatch(typeof(UILabel), nameof(UILabel.text), MethodType.Setter)]
    internal static class LabelTextPatch
    {
        private static void Postfix(UILabel __instance)
        {
            Bilingual.InspectLabel(__instance);
        }
    }

    [HarmonyPatch(typeof(FFG.MoM.MoM_CutsceneItem), "Start")]
    internal static class CutsceneStartPatch
    {
        private static void Postfix(FFG.MoM.MoM_CutsceneItem __instance)
        {
            if (!Plugin.Enabled.Value)
                return;
            if (__instance != null && __instance.TableStoryLabels != null)
                __instance.StartCoroutine(Relayout(__instance.TableStoryLabels));
        }

        private static IEnumerator Relayout(UITable table)
        {
            // UILabel heights settle at the end of the frame; re-stack the story
            // paragraphs afterwards so the English does not overlap the next label.
            yield return null;
            table.Reposition();
            yield return null;
            table.Reposition();
        }
    }

    // Long popups use a fixed-height label; shrink the combined text so the English
    // cannot run underneath the Continue button.
    [HarmonyPatch(typeof(FFG.MoM.MoM_MessagePopup), "InitializePopup")]
    internal static class MessagePopupPatch
    {
        private static void Postfix(FFG.MoM.MoM_MessagePopup __instance)
        {
            if (!Plugin.Enabled.Value)
                return;
            try
            {
                var field = AccessTools.Field(typeof(FFG.MoM.MoM_MessagePopup), "_textLabel");
                var label = field != null ? field.GetValue(__instance) as UILabel : null;
                if (label != null && label.overflowMethod != UILabel.Overflow.ShrinkContent)
                    label.overflowMethod = UILabel.Overflow.ShrinkContent;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("MessagePopup adjust failed: " + ex.Message);
            }
        }
    }

    [HarmonyPatch(typeof(Localization), "LoadCSV", new Type[] { typeof(TextAsset), typeof(bool), typeof(bool) })]
    internal static class LoadCsvPatch
    {
        private static void Postfix(TextAsset asset)
        {
            if (Plugin.DumpLocalization.Value)
                Bilingual.Dump(asset != null ? asset.name : "null");
            Bilingual.StartPreload();
        }
    }

    // Argument counting and inserted values must see the untouched Chinese text.
    [HarmonyPatch(typeof(FFG.MoM.MoM_LocalizationPacket), "CalculateArguments")]
    internal static class CalculateArgumentsPatch
    {
        private static void Prefix() { Bilingual.SuppressDepth++; }
        private static void Finalizer() { Bilingual.SuppressDepth--; }
    }

    [HarmonyPatch(typeof(FFG.MoM.MoM_LocalizationPacket), "ResolveInsert")]
    internal static class ResolveInsertPatch
    {
        private static void Prefix() { Bilingual.SuppressDepth++; }
        private static void Finalizer() { Bilingual.SuppressDepth--; }
    }

    // Scenario localization is normally loaded only for the player language.
    // Ask the game's loader for the English table as well; its own merge keeps
    // the result scoped to the current scenario.
    [HarmonyPatch(typeof(FFG.Common.PersistantGameObject), "CoroutineLoadScenarioLocalization")]
    internal static class ScenarioEnglishFallbackPatch
    {
        private static readonly HashSet<string> RequestedEnglish = new HashSet<string>();

        private static void Prefix(FFG.Common.PersistantGameObject __instance, string fileName, string languageCode)
        {
            if (!Plugin.Enabled.Value || __instance == null || string.IsNullOrEmpty(fileName))
                return;
            if (string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase))
                return;
            if (!RequestedEnglish.Add(fileName))
                return;

            // Make cached English available before the Chinese table is formatted,
            // so inserts such as SETUP_MSG1 resolve in English.
            Bilingual.MergeScenarioEnglish(fileName);
            __instance.StartCoroutine(__instance.CoroutineLoadScenarioLocalization(fileName, "en"));
        }
    }

    // Packet resolution must produce pure Chinese; English is appended after
    // the packet has formatted its inserts.
    [HarmonyPatch(typeof(FFG.MoM.MoM_LocalizationPacket), "ResolveLocalization", new Type[0])]
    internal static class ResolveLocalizationPatch
    {
        private static void Prefix() { Bilingual.SuppressDepth++; }
        private static void Finalizer() { Bilingual.SuppressDepth--; }
    }

    [HarmonyPatch]
    internal static class ResolveLocalizationOutPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(FFG.MoM.MoM_LocalizationPacket), "ResolveLocalization",
                new Type[] { typeof(GameObject).MakeByRefType() });
        }

        private static void Prefix() { Bilingual.SuppressDepth++; }
        private static void Finalizer() { Bilingual.SuppressDepth--; }
    }

    [HarmonyPatch(typeof(LocalizedTextNGUI), "UpdateLocalization",
                 new Type[] { typeof(FFG.MoM.MoM_LocalizationPacket) })]
    internal static class PacketLabelPatch
    {
        private static void Postfix(LocalizedTextNGUI __instance,
                                    FFG.MoM.MoM_LocalizationPacket localization)
        {
            if (!Plugin.Enabled.Value || localization == null || string.IsNullOrEmpty(localization.Key))
                return;
            if (Plugin.OnlyChinese.Value && Localization.language != "Chinese")
                return;
            if (string.IsNullOrEmpty(localization.LocalizedValue))
                return;

            string color = Plugin.EnglishColor.Value;
            if (!string.IsNullOrEmpty(color) &&
                localization.LocalizedValue.IndexOf("[" + color + "]", StringComparison.Ordinal) >= 0)
                return;

            string combined = Bilingual.WithEnglish(localization.Key, localization.LocalizedValue);
            if (combined == null || combined == localization.LocalizedValue)
                return;

            UILabel label = __instance.GetComponent<UILabel>();
            if (label != null)
            {
                label.text = combined;
                label.UpdateAnchors();
            }
        }
    }




    internal sealed class PreloadRunner : MonoBehaviour
    {
        private void Awake()
        {
            StartCoroutine(Plugin.PreloadEnglishScenarioLocalization());
        }
    }
}
