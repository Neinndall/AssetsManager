using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using Material.Icons;

namespace AssetsManager.Views
{
    public partial class HashGuessingWindow : UserControl
    {
        private readonly HashGuessingService _hashGuessingService;
        private readonly BinRstHashGuessingService _binRstHashGuessingService;
        private readonly AppSettings _appSettings;
        private readonly CustomMessageBoxService _messageBoxService;
        private readonly LogService _logService;
        private readonly ProgressUIManager _progressUIManager;
        private readonly TaskCancellationManager _taskCancellationManager;
        private readonly HashGuessLabModel _viewModel = new();
        private readonly List<HashMethodItemModel> _allMethods = new();
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isUpdatingResultsColumns;

        public HashGuessingWindow(
            HashGuessingService hashGuessingService,
            BinRstHashGuessingService binRstHashGuessingService,
            AppSettings appSettings,
            CustomMessageBoxService messageBoxService,
            LogService logService,
            ProgressUIManager progressUIManager,
            TaskCancellationManager taskCancellationManager)
        {
            InitializeComponent();
            _hashGuessingService = hashGuessingService;
            _binRstHashGuessingService = binRstHashGuessingService;
            _appSettings = appSettings;
            _messageBoxService = messageBoxService;
            _logService = logService;
            _progressUIManager = progressUIManager;
            _taskCancellationManager = taskCancellationManager;
            DataContext = _viewModel;
            InitializeMethods();
            Unloaded += OnUnloaded;
            Loaded += (s, e) =>
            {
                UpdateUnknownCountAsync();
                RefreshMethodsForCurrentDomain();
            };
        }

        private void InitializeMethods()
        {
            var accentBrush = (Brush)FindResource("AccentBrush") ?? Brushes.DodgerBlue;
            var accentTeal = (Brush)FindResource("AccentTeal") ?? Brushes.MediumSeaGreen;
            var accentOrange = (Brush)FindResource("AccentOrange") ?? Brushes.DarkOrange;
            var accentPurple = (Brush)FindResource("AccentPurple") ?? Brushes.MediumPurple;
            var accentGreen = (Brush)FindResource("AccentGreen") ?? Brushes.SeaGreen;
            var accentRed = (Brush)FindResource("AccentRed") ?? Brushes.Crimson;

            _allMethods.Clear();

            // GAME (Domain 0)
            _allMethods.Add(new HashMethodItemModel
            {
                Id = "game-scan",
                DomainIndex = 0,
                Name = "Search Unknown Hashes",
                Description = "Scan game WADs for unknown chunks and build an updated target inventory.",
                Category = "Inspection",
                IconKind = MaterialIconKind.Radar,
                BadgeText = "SCAN (~2s)",
                BadgeBrush = accentBrush,
                EstimatedTime = "~2s"
            });
            _allMethods.Add(new HashMethodItemModel
            {
                Id = "game-grep",
                DomainIndex = 0,
                Name = "Game GrepWad",
                Description = "Extract valid game asset paths embedded directly in local WAD files.",
                Category = "Inspection",
                IconKind = MaterialIconKind.FileSearchOutline,
                BadgeText = "⚡ FAST (~5s)",
                BadgeBrush = accentTeal,
                EstimatedTime = "~5s"
            });
            _allMethods.Add(new HashMethodItemModel
            {
                Id = "game-banner",
                DomainIndex = 0,
                Name = "Banner Guess",
                Description = "Targeted discovery of esports sponsored banners and arena textures.",
                Category = "Specialized",
                IconKind = MaterialIconKind.TrophyOutline,
                BadgeText = "⚡ FAST (~3s)",
                BadgeBrush = accentOrange,
                EstimatedTime = "~3s"
            });

            var gameBasic = new HashMethodItemModel
            {
                Id = "game-basic",
                DomainIndex = 0,
                Name = "GAME Basic Suite",
                Description = "Standard champion paths, skin numbers, character templates, locales and short ranges.",
                Category = "Core Suites",
                IconKind = MaterialIconKind.ShieldOutline,
                BadgeText = "🚀 FAST (~10s)",
                BadgeBrush = accentBrush,
                EstimatedTime = "~10s"
            };
            gameBasic.SubMethods.Add(new HashMethodSubItemModel { Id = "game-basic-crossdomain", Name = "GuessFromLcuHashes", Description = "Try client hash paths mapped to game structures", BadgeText = "⚡ FAST", BadgeBrush = accentTeal });
            gameBasic.SubMethods.Add(new HashMethodSubItemModel { Id = "game-basic-characters", Name = "GuessCharacterFiles", Description = "Champion and skin assets (characters/{champ}/skins/...)", BadgeText = "⚡ FAST", BadgeBrush = accentBrush });
            gameBasic.SubMethods.Add(new HashMethodSubItemModel { Id = "game-basic-regalia", Name = "GuessRegaliaAssets", Description = "Ranked banners, crests, borders, wings and loadout assets", BadgeText = "⚡ FAST", BadgeBrush = accentTeal });
            gameBasic.SubMethods.Add(new HashMethodSubItemModel { Id = "game-basic-shaders", Name = "GuessShaderVariants", Description = "Permutations across HLSL families and platform variants", BadgeText = "⚡ FAST", BadgeBrush = accentOrange });
            gameBasic.SubMethods.Add(new HashMethodSubItemModel { Id = "game-basic-locales", Name = "SubstituteLang", Description = "28 region and language translations", BadgeText = "⚡ FAST", BadgeBrush = accentTeal });
            gameBasic.SubMethods.Add(new HashMethodSubItemModel { Id = "game-basic-extensions", Name = "SubstituteExtensions", Description = "Cross-extension permutations (.dds, .tex, .bin, .anm)", BadgeText = "⚡ FAST", BadgeBrush = accentPurple });
            gameBasic.SubMethods.Add(new HashMethodSubItemModel { Id = "game-basic-prefixes", Name = "CheckBasenamePrefixes", Description = "Basename prefixes (2x_, 4x_, sd_, tft_, common_, base_, sru_, icon_)", BadgeText = "⚡ FAST", BadgeBrush = accentTeal });
            gameBasic.SubMethods.Add(new HashMethodSubItemModel { Id = "game-basic-numbers", Name = "SubstituteBasicNumbers", Description = "Sequential numbers (1 to 200) and padded variants (01 to 99)", BadgeText = "⚡ FAST", BadgeBrush = accentBrush });
            _allMethods.Add(gameBasic);

            var gameExtended = new HashMethodItemModel
            {
                Id = "game-extended",
                DomainIndex = 0,
                Name = "GAME Extended",
                Description = "Combinatorial skin groups, chromas from skins.json, suffixes and character substitutions.",
                Category = "Deep Search",
                IconKind = MaterialIconKind.CrownOutline,
                BadgeText = "⏳ DEEP (~1m)",
                BadgeBrush = accentOrange,
                EstimatedTime = "~1m"
            };
            gameExtended.SubMethods.Add(new HashMethodSubItemModel { Id = "game-ext-skingroups", Name = "SubstituteSkinGroups", Description = "Combinatorial skin groups from known character skins", BadgeText = "⏳ DEEP", BadgeBrush = accentOrange });
            gameExtended.SubMethods.Add(new HashMethodSubItemModel { Id = "game-ext-chromas", Name = "GuessChromaGroups", Description = "Chroma groupings parsed from local skins catalog", BadgeText = "⏳ DEEP", BadgeBrush = accentOrange });
            gameExtended.SubMethods.Add(new HashMethodSubItemModel { Id = "game-ext-suffixes", Name = "SubstituteSuffixes", Description = "Common asset suffixes substitution across paths", BadgeText = "🚀 FAST", BadgeBrush = accentTeal });
            gameExtended.SubMethods.Add(new HashMethodSubItemModel { Id = "game-ext-skinnumbers", Name = "SubstituteSkinNumbers", Description = "Combinations of skin numbers across champion templates", BadgeText = "🚀 FAST", BadgeBrush = accentPurple });
            gameExtended.SubMethods.Add(new HashMethodSubItemModel { Id = "game-ext-characters", Name = "SubstituteCharacter", Description = "Champion name substitutions across known game assets", BadgeText = "🚀 FAST", BadgeBrush = accentBrush });
            _allMethods.Add(gameExtended);

            var gameCustom = new HashMethodItemModel
            {
                Id = "game-custom",
                DomainIndex = 0,
                Name = "Game Custom Guess",
                Description = "Permutations and word variations across 30,000 BIN, DDS, TEX and custom shader paths.",
                Category = "Deep Search",
                IconKind = MaterialIconKind.FlaskOutline,
                BadgeText = "🚀 FAST (~15s)",
                BadgeBrush = accentTeal,
                EstimatedTime = "~15s"
            };
            gameCustom.SubMethods.Add(new HashMethodSubItemModel { Id = "game-custom-bin", Name = "SubstituteBinBasenameWords", Description = "30,000 top BIN samples word substitution", BadgeText = "🚀 FAST", BadgeBrush = accentTeal });
            gameCustom.SubMethods.Add(new HashMethodSubItemModel { Id = "game-custom-databin", Name = "SubstituteDataBinBasenameWords", Description = "data/*.bin basename word substitution", BadgeText = "🚀 FAST", BadgeBrush = accentTeal });
            gameCustom.SubMethods.Add(new HashMethodSubItemModel { Id = "game-custom-dds", Name = "SubstituteCharacterDdsBasenameWords", Description = "Character .dds texture word substitution", BadgeText = "🚀 FAST", BadgeBrush = accentBrush });
            gameCustom.SubMethods.Add(new HashMethodSubItemModel { Id = "game-custom-tex", Name = "SubstituteCharacterTexBasenameWords", Description = "Character .tex texture word substitution", BadgeText = "🚀 FAST", BadgeBrush = accentBrush });
            gameCustom.SubMethods.Add(new HashMethodSubItemModel { Id = "game-custom-wordaddition", Name = "AddCustomBasenameWord", Description = "Basename word insertion attack across game paths", BadgeText = "🚀 FAST", BadgeBrush = accentPurple });
            gameCustom.SubMethods.Add(new HashMethodSubItemModel { Id = "game-custom-swordlist", Name = "SubstituteSwordlistBasenameWords", Description = "Full corpus basename words substitution matrix", BadgeText = "🚀 FAST", BadgeBrush = accentTeal });
            gameCustom.SubMethods.Add(new HashMethodSubItemModel { Id = "game-custom-shaders", Name = "SubstituteShaderVocabWords", Description = "Custom shader vocabulary and compound names", BadgeText = "⚡ FAST", BadgeBrush = accentOrange });
            _allMethods.Add(gameCustom);

            // LCU (Domain 1)
            _allMethods.Add(new HashMethodItemModel
            {
                Id = "lcu-scan",
                DomainIndex = 1,
                Name = "Search Unknown Hashes",
                Description = "Scan client WADs for unknown chunks and build an updated target inventory.",
                Category = "Inspection",
                IconKind = MaterialIconKind.Radar,
                BadgeText = "SCAN (~2s)",
                BadgeBrush = accentPurple,
                EstimatedTime = "~2s"
            });
            _allMethods.Add(new HashMethodItemModel
            {
                Id = "lcu-grep",
                DomainIndex = 1,
                Name = "Lcu GrepWad",
                Description = "Extract client paths and bundle references embedded in client WAD chunks.",
                Category = "Inspection",
                IconKind = MaterialIconKind.FileSearchOutline,
                BadgeText = "⚡ FAST (~5s)",
                BadgeBrush = accentPurple,
                EstimatedTime = "~5s"
            });

            var lcuCustom = new HashMethodItemModel
            {
                Id = "lcu-custom",
                DomainIndex = 1,
                Name = "LCU Custom Guess",
                Description = "Targeted client attacks: Scoped Plugins, Directory Mirroring and Universal UI Modifiers.",
                Category = "Core Suites",
                IconKind = MaterialIconKind.FlaskOutline,
                BadgeText = "🚀 FAST (~15s)",
                BadgeBrush = accentTeal,
                EstimatedTime = "~15s"
            };
            lcuCustom.SubMethods.Add(new HashMethodSubItemModel { Id = "lcu-custom-scoped", Name = "GuessScopedPlugins", Description = "Intra-plugin directory topology, vocabulary, numeric ranges & component synthesis", BadgeText = "⚡ ~5s", BadgeBrush = accentTeal });
            lcuCustom.SubMethods.Add(new HashMethodSubItemModel { Id = "lcu-custom-mirroring", Name = "MirrorDirectories", Description = "Deep directory mirroring across /images/, /assets/, and root structures", BadgeText = "⚡ ~1s", BadgeBrush = accentPurple });
            lcuCustom.SubMethods.Add(new HashMethodSubItemModel { Id = "lcu-custom-modifiers", Name = "GuessUniversalModifiers", Description = "Riot UI states (hover, active, disabled, tier1-4, mini, lg)", BadgeText = "⚡ ~2s", BadgeBrush = accentBrush });
            _allMethods.Add(lcuCustom);

            var lcuBasic = new HashMethodItemModel
            {
                Id = "lcu-basic",
                DomainIndex = 1,
                Name = "LCU Basic Suite",
                Description = "Structural client patterns, plugin variants, numeric sequences & GAME cross-domain.",
                Category = "Core Suites",
                IconKind = MaterialIconKind.FileCabinet,
                BadgeText = "🚀 FAST (~10s)",
                BadgeBrush = accentPurple,
                EstimatedTime = "~10s"
            };
            lcuBasic.SubMethods.Add(new HashMethodSubItemModel { Id = "lcu-basic-extensions", Name = "SubstituteExtensions", Description = "Permutations across client file extensions", BadgeText = "⚡ FAST", BadgeBrush = accentTeal });
            lcuBasic.SubMethods.Add(new HashMethodSubItemModel { Id = "lcu-basic-patterns", Name = "GuessKnownPatterns", Description = "Common structural client URL patterns", BadgeText = "⚡ FAST", BadgeBrush = accentBrush });
            lcuBasic.SubMethods.Add(new HashMethodSubItemModel { Id = "lcu-basic-crossdomain", Name = "GuessFromGameHashes", Description = "Game paths mapped into LCU plugins", BadgeText = "⚡ FAST", BadgeBrush = accentTeal });
            lcuBasic.SubMethods.Add(new HashMethodSubItemModel { Id = "lcu-basic-plugins", Name = "SubstitutePlugins", Description = "Substitutions across rcp-fe-* and rcp-be-*", BadgeText = "⚡ FAST", BadgeBrush = accentBrush });
            lcuBasic.SubMethods.Add(new HashMethodSubItemModel { Id = "lcu-basic-numbers", Name = "SubstituteNumbers", Description = "Numeric sequences (1 to 10,000)", BadgeText = "⚡ FAST", BadgeBrush = accentOrange });
            lcuBasic.SubMethods.Add(new HashMethodSubItemModel { Id = "lcu-basic-locales", Name = "SubstituteLang", Description = "Regional and language token substitutions", BadgeText = "⚡ FAST", BadgeBrush = accentTeal });
            lcuBasic.SubMethods.Add(new HashMethodSubItemModel { Id = "lcu-basic-basenames", Name = "SubstituteBasenames", Description = "Direct basename substitutions across plugins", BadgeText = "🚀 FAST", BadgeBrush = accentBrush });
            lcuBasic.SubMethods.Add(new HashMethodSubItemModel { Id = "lcu-basic-basenamewords", Name = "SubstituteBasenameWords", Description = "Word substitution in basenames", BadgeText = "🚀 FAST", BadgeBrush = accentPurple });
            _allMethods.Add(lcuBasic);

            var lcuExtended = new HashMethodItemModel
            {
                Id = "lcu-extended",
                DomainIndex = 1,
                Name = "LCU Extended",
                Description = "Deep dictionary word insertion and legacy TFT word-pair patterns.",
                Category = "Deep Search",
                IconKind = MaterialIconKind.Sparkles,
                BadgeText = "⏳ DEEP (~1m)",
                BadgeBrush = accentRed,
                EstimatedTime = "~1m"
            };
            lcuExtended.SubMethods.Add(new HashMethodSubItemModel { Id = "lcu-ext-wordaddition", Name = "AddBasenameWord", Description = "Deep dictionary word insertion across client paths", BadgeText = "⏳ DEEP (~1m)", BadgeBrush = accentRed });
            lcuExtended.SubMethods.Add(new HashMethodSubItemModel { Id = "lcu-ext-v1tft", Name = "GuessV1TftPaths", Description = "Legacy TFT word-pair combinations", BadgeText = "🚀 FAST (~8s)", BadgeBrush = accentTeal });
            _allMethods.Add(lcuExtended);

            // BIN (Domain 2)
            _allMethods.Add(new HashMethodItemModel
            {
                Id = "bin-inventory",
                DomainIndex = 2,
                Name = "Search Unknown Hashes",
                Description = "Scan BIN WAD chunks and loose BIN files to build the unknown hash inventory.",
                Category = "Inspection",
                IconKind = MaterialIconKind.Radar,
                BadgeText = "SCAN (~3s)",
                BadgeBrush = accentBrush,
                EstimatedTime = "~3s"
            });

            var binContext = new HashMethodItemModel
            {
                Id = "bin-context",
                DomainIndex = 2,
                Name = "BIN Context Attack",
                Description = "Discover hashes using verified BIN relationships, resolved links and property patterns.",
                Category = "Core Suites",
                IconKind = MaterialIconKind.CodeBraces,
                BadgeText = "🚀 FAST (~5s)",
                BadgeBrush = accentGreen,
                EstimatedTime = "~5s"
            };
            binContext.SubMethods.Add(new HashMethodSubItemModel { Id = "bin-context-owning", Name = "OwningEntryStrings", Description = "Resolve object entry names from embedded strings", BadgeText = "⚡ FAST", BadgeBrush = accentGreen });
            binContext.SubMethods.Add(new HashMethodSubItemModel { Id = "bin-context-objectlocal", Name = "ObjectLocalHashPairs", Description = "Correlate strings and hash pairs inside the same struct", BadgeText = "⚡ FAST", BadgeBrush = accentGreen });
            binContext.SubMethods.Add(new HashMethodSubItemModel { Id = "bin-context-pathleaf", Name = "ResolvedHashPathLeaf", Description = "Infer child property names from resolved child hashes", BadgeText = "⚡ FAST", BadgeBrush = accentTeal });
            binContext.SubMethods.Add(new HashMethodSubItemModel { Id = "bin-context-structures", Name = "ContextualStructures", Description = "Heuristics for Spells, VFX, Characters and MapSkins", BadgeText = "⚡ FAST", BadgeBrush = accentPurple });
            binContext.SubMethods.Add(new HashMethodSubItemModel { Id = "bin-context-tft-shop", Name = "TftShopPaths", Description = "Resolve TFT shop entries from set and item names", BadgeText = "⚡ FAST", BadgeBrush = accentGreen });
            binContext.SubMethods.Add(new HashMethodSubItemModel { Id = "bin-context-augment", Name = "AugmentSpellPaths", Description = "Resolve augment entries and their root spells", BadgeText = "⚡ FAST", BadgeBrush = accentPurple });
            binContext.SubMethods.Add(new HashMethodSubItemModel { Id = "bin-context-quests", Name = "ModeQuestPaths", Description = "Resolve mode quest entries from quest names", BadgeText = "⚡ FAST", BadgeBrush = accentTeal });
            binContext.SubMethods.Add(new HashMethodSubItemModel { Id = "bin-context-attributes", Name = "AttributeEntryPaths", Description = "Map known BIN attributes directly to entry paths", BadgeText = "⚡ FAST", BadgeBrush = accentBrush });
            binContext.SubMethods.Add(new HashMethodSubItemModel { Id = "bin-context-relations", Name = "ObjectLinkRelations", Description = "Resolve entry links exposed by map and loadout structures", BadgeText = "⚡ FAST", BadgeBrush = accentPurple });
            binContext.SubMethods.Add(new HashMethodSubItemModel { Id = "bin-context-strings", Name = "LiteralBinStrings", Description = "Scan all binary strings against local target domains", BadgeText = "⚡ ~3s", BadgeBrush = accentOrange });
            _allMethods.Add(binContext);

            var binSchema = new HashMethodItemModel
            {
                Id = "bin-schema",
                DomainIndex = 2,
                Name = "Meta Schema Guess",
                Description = "Generate review-only type and field name candidates with zero noise explosion.",
                Category = "Core Suites",
                IconKind = MaterialIconKind.ToyBrickOutline,
                BadgeText = "🚀 FAST (~5s)",
                BadgeBrush = accentPurple,
                EstimatedTime = "~5s"
            };
            binSchema.SubMethods.Add(new HashMethodSubItemModel { Id = "bin-schema-reverse-suffix", Name = "SuffixFoldingEngine", Description = "Reverse-fold 45+ class/field suffixes in state space (O(Words))", BadgeText = "🚀 FAST", BadgeBrush = accentGreen });
            binSchema.SubMethods.Add(new HashMethodSubItemModel { Id = "bin-schema-family-lattice", Name = "BaseClassFamilyLattice", Description = "Inherit sibling suffixes & vocabulary from base classes", BadgeText = "⚡ FAST", BadgeBrush = accentTeal });
            binSchema.SubMethods.Add(new HashMethodSubItemModel { Id = "bin-schema-crossdomain", Name = "CrossDomainDictionary", Description = "Known types as fields, known fields as types, 3D bones", BadgeText = "⚡ FAST", BadgeBrush = accentBrush });
            binSchema.SubMethods.Add(new HashMethodSubItemModel { Id = "bin-schema-bigram-chain", Name = "MarkovBigramChains", Description = "Attested 2..4 word transitions from meta dictionary", BadgeText = "⚡ FAST", BadgeBrush = accentPurple });
            binSchema.SubMethods.Add(new HashMethodSubItemModel { Id = "bin-schema-word-reduction", Name = "WordReductionPass", Description = "Delete 1 inner word from known schema names", BadgeText = "⚡ FAST", BadgeBrush = accentOrange });
            binSchema.SubMethods.Add(new HashMethodSubItemModel { Id = "bin-schema-word-swap", Name = "WordSubstitutionPass", Description = "Substitute corpus words into attested name positions", BadgeText = "⚡ FAST", BadgeBrush = accentTeal });
            binSchema.SubMethods.Add(new HashMethodSubItemModel { Id = "bin-schema-path-templates", Name = "StructuralTemplates", Description = "Path and field numeric and character substitutions", BadgeText = "⚡ FAST", BadgeBrush = accentBrush });
            _allMethods.Add(binSchema);

            // RST (Domain 3)
            _allMethods.Add(new HashMethodItemModel
            {
                Id = "rst-inventory",
                DomainIndex = 3,
                Name = "Search Unknown Hashes",
                Description = "Scan RST assets and build the unknown hash inventory.",
                Category = "Inspection",
                IconKind = MaterialIconKind.Radar,
                BadgeText = "SCAN (~3s)",
                BadgeBrush = accentBrush,
                EstimatedTime = "~3s"
            });

            var rstContent = new HashMethodItemModel
            {
                Id = "rst-content",
                DomainIndex = 3,
                Name = "RST Content GREP",
                Description = "Extract valid font/translation string keys from BIN payloads and text resources.",
                Category = "Core Suites",
                IconKind = MaterialIconKind.FormatLetterCase,
                BadgeText = "🚀 FAST (~5s)",
                BadgeBrush = accentGreen,
                EstimatedTime = "~5s"
            };
            rstContent.SubMethods.Add(new HashMethodSubItemModel { Id = "rst-content-binstrings", Name = "BinStringExtraction", Description = "Extract font, tooltip and translation keys from BIN payloads", BadgeText = "⚡ FAST", BadgeBrush = accentGreen });
            rstContent.SubMethods.Add(new HashMethodSubItemModel { Id = "rst-content-text", Name = "TextResourceGrep", Description = "Scan JSON, XML, YAML, INI, and script text resources", BadgeText = "⚡ FAST", BadgeBrush = accentPurple });
            _allMethods.Add(rstContent);

            var rstStructural = new HashMethodItemModel
            {
                Id = "rst-structural",
                DomainIndex = 3,
                Name = "RST Structural Attack",
                Description = "Cross-version and numeric key variations across localized string tables.",
                Category = "Core Suites",
                IconKind = MaterialIconKind.Numeric,
                BadgeText = "🚀 FAST (~10s)",
                BadgeBrush = accentGreen,
                EstimatedTime = "~10s"
            };
            rstStructural.SubMethods.Add(new HashMethodSubItemModel { Id = "rst-struct-crossversion", Name = "CrossVersionKeys", Description = "Test known XXH3 against XXH64 and vice versa", BadgeText = "⚡ FAST", BadgeBrush = accentGreen });
            rstStructural.SubMethods.Add(new HashMethodSubItemModel { Id = "rst-struct-binkeys", Name = "BinDictionaryKeys", Description = "Test known BIN property/entry names as RST keys", BadgeText = "⚡ FAST", BadgeBrush = accentTeal });
            rstStructural.SubMethods.Add(new HashMethodSubItemModel { Id = "rst-struct-gamepaths", Name = "GamePathsToRst", Description = "Cross-reference GAME and LCU asset paths against versioned RST keys", BadgeText = "⚡ FAST", BadgeBrush = accentBrush });
            _allMethods.Add(rstStructural);
        }

        private void RefreshMethodsForCurrentDomain()
        {
            int domainIndex = DomainSelector?.SelectedIndex ?? 0;
            string query = TxtMethodSearch?.Text?.Trim() ?? string.Empty;

            var filtered = _allMethods
                .Where(m => m.DomainIndex == domainIndex)
                .Where(m => string.IsNullOrEmpty(query) ||
                            m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            m.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            m.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            _viewModel.AvailableMethods.Clear();
            _viewModel.AvailableMethods.AddRange(filtered);

            if (_viewModel.SelectedMethod == null || !_viewModel.AvailableMethods.Contains(_viewModel.SelectedMethod))
            {
                _viewModel.SelectedMethod = _viewModel.AvailableMethods.FirstOrDefault();
            }
        }

        private async void UpdateUnknownCountAsync()
        {
            if (DomainSelector == null || TxtUnknownCount == null || TxtUnknownBreakdown == null) return;
            int selectedIndex = DomainSelector.SelectedIndex;
            try
            {
                if (selectedIndex < 2)
                {
                    var domain = selectedIndex == 0 ? HashGuessDomain.Game : HashGuessDomain.Lcu;
                    var summary = await Task.Run(() => _hashGuessingService.GetUnknownSummaryAsync(domain, CancellationToken.None));
                    if (DomainSelector != null && DomainSelector.SelectedIndex == selectedIndex)
                    {
                        TxtUnknownCount.Text = $"{summary.Total:N0} unresolved";
                        TxtUnknownBreakdown.Text = $"Current: {summary.Current:N0} · Recent: {summary.Recent:N0}";
                    }
                }
                else
                {
                    var summary = await Task.Run(() => _binRstHashGuessingService.GetSummaryAsync(CancellationToken.None));
                    if (DomainSelector != null && DomainSelector.SelectedIndex == selectedIndex)
                    {
                        if (selectedIndex == 2)
                        {
                            TxtUnknownCount.Text = $"{summary.BinTotal:N0} unresolved";
                            TxtUnknownBreakdown.Text = $"Entries: {summary.BinEntries:N0} · Types: {summary.BinTypes:N0}\nFields: {summary.BinFields:N0} · Hashes: {summary.BinHashes:N0}";
                        }
                        else
                        {
                            TxtUnknownCount.Text = $"{summary.RstTotal:N0} unresolved";
                            TxtUnknownBreakdown.Text = $"XXH3: {summary.RstXxh3:N0} · XXH64: {summary.RstXxh64:N0}";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Hash Lab could not refresh the unknown hash count.");
                TxtUnknownCount.Text = "Unknown";
                TxtUnknownBreakdown.Text = string.Empty;
            }
        }

        private void ShowLiveUnknownCount(int remaining, int resolved)
        {
            TxtUnknownCount.Text = $"{remaining:N0} unresolved";
            TxtUnknownBreakdown.Text = $"Resolved: {resolved:N0} · Pending: {remaining:N0}";
        }

        private void ResultsListView_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(UpdateSourceWadColumnWidth, DispatcherPriority.Loaded);
        }

        private void ResultsListView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateSourceWadColumnWidth();
        }

        private void ResultsColumnHeader_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_isUpdatingResultsColumns)
            {
                UpdateSourceWadColumnWidth();
            }
        }

        private void UpdateSourceWadColumnWidth()
        {
            if (_isUpdatingResultsColumns ||
                ResultsListView?.View is not GridView gridView ||
                gridView.Columns.Count != 5)
            {
                return;
            }

            var scrollViewer = FindScrollViewer(ResultsListView);
            if (scrollViewer == null || scrollViewer.ViewportWidth <= 0)
            {
                return;
            }

            double precedingWidth = gridView.Columns.Take(4).Sum(column => column.ActualWidth);
            double sourceWadWidth = Math.Max(100, scrollViewer.ViewportWidth - precedingWidth);

            if (Math.Abs(gridView.Columns[4].Width - sourceWadWidth) < 0.5)
            {
                return;
            }

            _isUpdatingResultsColumns = true;
            try
            {
                gridView.Columns[4].Width = sourceWadWidth;
            }
            finally
            {
                _isUpdatingResultsColumns = false;
            }
        }

        private static ScrollViewer FindScrollViewer(DependencyObject element)
        {
            if (element is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            {
                var result = FindScrollViewer(VisualTreeHelper.GetChild(element, index));
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        private void DomainSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateUnknownCountAsync();
            RefreshMethodsForCurrentDomain();
        }

        private void TxtMethodSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshMethodsForCurrentDomain();
        }



        private async void RunSelectedMethod_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedMethod != null && !_viewModel.IsRunning)
            {
                await ExecuteMethodByIdAsync(_viewModel.SelectedMethod.Id);
            }
        }

        private async Task ExecuteMethodByIdAsync(string methodId)
        {
            var methodModel = _allMethods.FirstOrDefault(m => string.Equals(m.Id, methodId, StringComparison.OrdinalIgnoreCase));
            IReadOnlySet<string> selectedSubMethods = methodModel != null && methodModel.HasSubMethods
                ? methodModel.SubMethods.Where(s => s.IsSelected).Select(s => s.Id).ToHashSet()
                : null;

            switch (methodId)
            {
                // GAME
                case "game-prefixes":
                    await RunAsync(HashGuessMode.GamePrefixes);
                    break;
                case "game-shaders":
                    await RunAsync(HashGuessMode.GameShaders);
                    break;
                case "game-grep":
                    await RunAsync(HashGuessMode.GrepGame);
                    break;
                case "game-scan":
                    await RunScanUnknownsAsync(HashGuessDomain.Game);
                    break;
                case "game-basic":
                    await RunAsync(HashGuessMode.GameBasic);
                    break;
                case "game-extended":
                    await RunAsync(HashGuessMode.GameExtended);
                    break;
                case "game-banner":
                    await RunAsync(HashGuessMode.BannerGuess);
                    break;
                case "game-custom":
                    await RunAsync(HashGuessMode.GameCustom);
                    break;

                // LCU
                case "lcu-scoped":
                    await RunAsync(HashGuessMode.LcuScoped);
                    break;
                case "lcu-modifiers":
                    await RunAsync(HashGuessMode.LcuModifiers);
                    break;
                case "lcu-media":
                    await RunAsync(HashGuessMode.LcuMedia);
                    break;
                case "lcu-basic":
                    await RunAsync(HashGuessMode.LcuBasic);
                    break;
                case "lcu-custom":
                    await RunAsync(HashGuessMode.LcuCustom);
                    break;
                case "lcu-extended":
                    await RunAsync(HashGuessMode.LcuExtended);
                    break;
                case "lcu-v1":
                    await RunAsync(HashGuessMode.LcuV1Paths);
                    break;
                case "lcu-grep":
                    await RunAsync(HashGuessMode.GrepLcu);
                    break;
                case "lcu-scan":
                    await RunScanUnknownsAsync(HashGuessDomain.Lcu);
                    break;

                // BIN
                case "bin-inventory":
                    await RunInternalAsync(InternalHashAction.Inventory, selectedSubMethods);
                    break;
                case "bin-context":
                    await RunInternalAsync(InternalHashAction.Content, selectedSubMethods);
                    break;
                case "bin-schema":
                    await RunInternalAsync(InternalHashAction.Structural, selectedSubMethods);
                    break;

                // RST
                case "rst-inventory":
                    await RunInternalAsync(InternalHashAction.Inventory, selectedSubMethods);
                    break;
                case "rst-content":
                    await RunInternalAsync(InternalHashAction.Content, selectedSubMethods);
                    break;
                case "rst-structural":
                    await RunInternalAsync(InternalHashAction.Structural, selectedSubMethods);
                    break;

                default:
                    _messageBoxService.ShowWarning("Hash Guessing Lab", $"Unknown algorithm action '{methodId}'.", Window.GetWindow(this));
                    break;
            }
        }

        private void SelectAllSubMethods_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.SelectedMethod?.SelectAllSubMethods(true);
        }

        private void DeselectAllSubMethods_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.SelectedMethod?.SelectAllSubMethods(false);
        }

        private async void ExportJson_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.Matches.Count == 0)
            {
                _messageBoxService?.ShowWarning("Export JSON", "There are no resolved matches to export.", Window.GetWindow(this));
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Resolved Hashes as JSON",
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                FileName = $"resolved_hashes_{DateTime.Now:yyyyMMdd_HHmmss}.json"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var items = _viewModel.Matches.Select(m =>
                    {
                        if (m is HashGuessMatch gm)
                        {
                            return new
                            {
                                hash = gm.HashText,
                                path = gm.Path,
                                domain = gm.DomainText,
                                strategy = gm.StrategyText,
                                sourceWad = gm.SourceWadPath,
                                foundAtUtc = gm.FoundAtUtc
                            };
                        }
                        if (m is InternalHashGuessMatch im)
                        {
                            return new
                            {
                                hash = im.HashText,
                                path = im.Value,
                                domain = im.DomainText,
                                strategy = im.Strategy.ToString(),
                                sourceWad = im.SourceWad,
                                foundAtUtc = im.FoundAtUtc
                            };
                        }
                        return (object)m;
                    }).ToList();

                    var exportData = new
                    {
                        exportedAt = DateTime.UtcNow.ToString("o"),
                        totalMatches = items.Count,
                        matches = items
                    };

                    string json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(dialog.FileName, json);
                    _logService.LogInteractiveSuccess($"Successfully exported {_viewModel.Matches.Count} matches to", dialog.FileName, Path.GetFileName(dialog.FileName));
                }
                catch (Exception ex)
                {
                    _logService?.LogError(ex, "Failed to export hash matches to JSON.");
                    _messageBoxService?.ShowError("Export Error", $"Failed to export JSON: {ex.Message}", Window.GetWindow(this));
                }
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _taskCancellationManager?.CancelCurrentOperation();
            _cancellationTokenSource?.Cancel();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // Do not cancel background task on view unload
        }

        private async Task RunAsync(HashGuessMode mode)
        {
            if (_viewModel.IsRunning) return;

            var domain = DomainSelector.SelectedIndex == 0 ? HashGuessDomain.Game : HashGuessDomain.Lcu;
            if (mode is HashGuessMode.GrepGame or HashGuessMode.BannerGuess or HashGuessMode.GameCustom) domain = HashGuessDomain.Game;
            else if (mode is HashGuessMode.GrepLcu or HashGuessMode.LcuCustom or HashGuessMode.LcuScoped or HashGuessMode.LcuModifiers or HashGuessMode.LcuMedia) domain = HashGuessDomain.Lcu;

            string rootPath = _appSettings.LolPbeDirectory?.Trim();
            if (string.IsNullOrWhiteSpace(rootPath) || !System.IO.Directory.Exists(rootPath))
            {
                _messageBoxService.ShowError("Hash Guessing Lab", "Please configure the LoL PBE Install Directory in Settings first.", Window.GetWindow(this));
                return;
            }

            var selectedSubMethods = _viewModel.SelectedMethod?.SubMethods?
                .Where(s => s.IsSelected)
                .Select(s => s.Id)
                .ToHashSet();

            if (_viewModel.SelectedMethod?.HasSubMethods == true && (selectedSubMethods == null || selectedSubMethods.Count == 0))
            {
                _messageBoxService.ShowWarning("Hash Guessing Lab", "Please select at least one sub-algorithm to execute.", Window.GetWindow(this));
                return;
            }

            var taskToken = _taskCancellationManager != null ? _taskCancellationManager.PrepareNewOperation() : CancellationToken.None;
            var runCancellation = new CancellationTokenSource();
            _cancellationTokenSource = runCancellation;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(runCancellation.Token, taskToken);
            var effectiveToken = linkedCts.Token;

            _viewModel.IsRunning = true;
            _viewModel.ProgressValue = 0;
            _viewModel.ProgressText = "Scanning";
            _viewModel.IsProgressIndeterminate = mode != HashGuessMode.GrepGame && mode != HashGuessMode.GrepLcu;
            
            string currentStage = (mode == HashGuessMode.GrepGame || mode == HashGuessMode.GrepLcu) ? "Building unknown hash inventory..." : "Building structural candidates...";
            long totalChecked = 0;
            int totalWads = 0;
            int foundMatches = 0;
            int sessionTargets = 0;

            _viewModel.Matches.Clear();
            var displayedMatchHashes = new HashSet<ulong>();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            void UpdateStatus()
            {
                string timeText = FormatElapsedTime(stopwatch.Elapsed);
                if (totalWads > 0 && totalChecked == 0 && foundMatches == 0)
                {
                    _viewModel.StatusText = $"{currentStage} · Time: {timeText}";
                }
                else
                {
                    _viewModel.StatusText = $"{currentStage} · {foundMatches:N0} found · Time: {timeText}";
                }
            }

            void UpdateLiveProgress(int? remaining = null, int? matches = null)
            {
                if (matches.HasValue)
                    foundMatches = Math.Max(foundMatches, matches.Value);
                if (remaining.HasValue)
                    sessionTargets = Math.Max(sessionTargets, remaining.Value + foundMatches);

                if (sessionTargets > 0)
                {
                    int unresolved = Math.Max(0, sessionTargets - foundMatches);
                    ShowLiveUnknownCount(unresolved, foundMatches);
                }
                UpdateStatus();
            }

            UpdateStatus();
            string initialStatus = (mode is HashGuessMode.GrepGame or HashGuessMode.GrepLcu) ? "Building unknown hash inventory..." : "Preparing Candidates...";
            _progressUIManager?.OnHashGuessingStarted(_viewModel.SelectedMethod?.Name ?? mode.ToString(), initialStatus);

            try
            {
                var progress = new Progress<HashGuessProgress>(value =>
                {
                    currentStage = !string.IsNullOrEmpty(value.CurrentWad) ? value.CurrentWad : currentStage;
                    totalChecked = value.CheckedCandidates > 0 ? value.CheckedCandidates : value.ProcessedChunks;
                    totalWads = value.TotalWads;
                    UpdateLiveProgress(value.RemainingUnknowns, value.FoundMatches);

                    _viewModel.IsProgressIndeterminate = value.TotalWads == 0;
                    if (value.TotalWads > 0)
                    {
                        _viewModel.ProgressValue = value.ProcessedWads * 100d / value.TotalWads;
                        _viewModel.ProgressText = $"{_viewModel.ProgressValue:F0}%";
                    }
                    else
                    {
                        long checkedCount = value.CheckedCandidates > 0 ? value.CheckedCandidates : value.ProcessedChunks;
                        _viewModel.ProgressText = $"{checkedCount:N0} checked";
                    }

                    string statusMsg;
                    string customProgressText = null;

                    if (value.TotalWads > 0)
                    {
                        string fileName = string.IsNullOrEmpty(value.CurrentWad) ? "WAD" : value.CurrentWad;
                        if (fileName.Equals("Building unknown hash inventory...", StringComparison.OrdinalIgnoreCase))
                        {
                            statusMsg = "Building unknown hash inventory...";
                        }
                        else
                        {
                            statusMsg = $"Scanning {value.ProcessedWads} of {value.TotalWads} WADs: {fileName}";
                        }
                        _progressUIManager?.OnHashGuessingProgressChanged(statusMsg, value.ProcessedWads, value.TotalWads, statusMsg, null);
                    }
                    else
                    {
                        string stageName = string.IsNullOrEmpty(value.CurrentWad) ? (_viewModel.SelectedMethod?.Name ?? "In-memory") : value.CurrentWad;
                        statusMsg = $"Hash Lab: {stageName} · {foundMatches:N0} found";
                        customProgressText = $"{totalChecked:N0} checked";
                        _progressUIManager?.OnHashGuessingProgressChanged(statusMsg, 0, 0, $"{stageName} · {foundMatches:N0} found", customProgressText);
                    }
                });
                var matchProgress = new Progress<HashGuessMatch>(match =>
                {
                    if (displayedMatchHashes.Add(match.Hash))
                    {
                        _viewModel.Matches.Add(match);
                        foundMatches++;
                        UpdateLiveProgress(matches: foundMatches);
                    }
                });
                var result = mode switch
                {
                    HashGuessMode.GamePrefixes => await _hashGuessingService.RunGamePrefixGuessingAsync(rootPath, progress, effectiveToken, matchProgress),
                    HashGuessMode.GameShaders => await _hashGuessingService.RunGameShaderGuessingAsync(rootPath, progress, effectiveToken, matchProgress),
                    HashGuessMode.GameBasic => await _hashGuessingService.RunGameBasicGuessingAsync(rootPath, progress, effectiveToken, matchProgress, selectedSubMethods),
                    HashGuessMode.GameExtended => await _hashGuessingService.RunGameExtendedGuessingAsync(rootPath, progress, effectiveToken, matchProgress, selectedSubMethods),
                    HashGuessMode.BannerGuess => await _hashGuessingService.RunGameBannerGuessingAsync(rootPath, progress, effectiveToken, matchProgress),
                    HashGuessMode.GameCustom => await _hashGuessingService.RunGameCustomGuessingAsync(rootPath, progress, effectiveToken, matchProgress, selectedSubMethods),
                    HashGuessMode.LcuScoped => await _hashGuessingService.RunLcuScopedPluginGuessingAsync(rootPath, progress, effectiveToken, matchProgress),
                    HashGuessMode.LcuModifiers => await _hashGuessingService.RunLcuUniversalModifierGuessingAsync(rootPath, progress, effectiveToken, matchProgress),
                    HashGuessMode.LcuMedia => await _hashGuessingService.RunLcuMediaGuessingAsync(rootPath, progress, effectiveToken, matchProgress),
                    HashGuessMode.LcuBasic => await _hashGuessingService.RunLcuBasicGuessingAsync(rootPath, progress, effectiveToken, matchProgress, selectedSubMethods),
                    HashGuessMode.LcuExtended => await _hashGuessingService.RunLcuExtendedGuessingAsync(rootPath, progress, effectiveToken, matchProgress, selectedSubMethods),
                    HashGuessMode.LcuCustom => await _hashGuessingService.RunLcuCustomGuessingAsync(rootPath, progress, effectiveToken, matchProgress, selectedSubMethods),
                    HashGuessMode.LcuV1Paths => await _hashGuessingService.RunLcuV1PathGuessingAsync(rootPath, progress, effectiveToken, matchProgress),
                    HashGuessMode.GrepGame => await _hashGuessingService.RunEmbeddedPathGrepAsync(HashGuessDomain.Game, rootPath, progress, effectiveToken, matchProgress),
                    HashGuessMode.GrepLcu => await _hashGuessingService.RunEmbeddedPathGrepAsync(HashGuessDomain.Lcu, rootPath, progress, effectiveToken, matchProgress),
                    _ => throw new ArgumentOutOfRangeException(nameof(mode))
                };
                stopwatch.Stop();
                string elapsedTime = FormatElapsedTime(stopwatch.Elapsed);
                _viewModel.Matches.AddRange(result.Matches.Where(match => displayedMatchHashes.Add(match.Hash)));
                UpdateLiveProgress(result.UnknownHashesAtStart - result.Matches.Count, result.Matches.Count);
                _viewModel.ProgressValue = 100;
                _viewModel.ProgressText = "100%";
                _viewModel.IsProgressIndeterminate = false;
                if (result.Matches.Count > 0)
                {
                    await _hashGuessingService.SaveMatchesAsync(result.Matches, CancellationToken.None);
                    _viewModel.StatusText = $"Completed in {elapsedTime}: {result.Matches.Count:N0} paths resolved and automatically added to main hash files.";
                }
                else
                {
                    _viewModel.StatusText = $"Completed in {elapsedTime}: {result.Matches.Count:N0} paths resolved from {result.UnknownHashesAtStart:N0} unknown hashes.";
                }
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                _viewModel.ProgressText = "";
                _viewModel.ProgressValue = 0;
                _viewModel.IsProgressIndeterminate = false;
                _viewModel.StatusText = "Operation was canceled by user.";
            }
            catch (InvalidOperationException ex)
            {
                stopwatch.Stop();
                _viewModel.ProgressText = "";
                _viewModel.ProgressValue = 0;
                _viewModel.IsProgressIndeterminate = false;
                _logService.LogWarning(ex.Message);
                _viewModel.StatusText = "Pre-validation failed. Run WAD Path Grep first.";
                _messageBoxService.ShowWarning("Hash Guessing Lab", ex.Message, Window.GetWindow(this));
            }
            catch (DirectoryNotFoundException ex)
            {
                stopwatch.Stop();
                _viewModel.ProgressText = "";
                _viewModel.ProgressValue = 0;
                _viewModel.IsProgressIndeterminate = false;
                _logService.LogWarning(ex.Message);
                _viewModel.StatusText = "Selected directory does not exist.";
                _messageBoxService.ShowWarning("Hash Guessing Lab", ex.Message, Window.GetWindow(this));
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _viewModel.ProgressText = "";
                _viewModel.ProgressValue = 0;
                _viewModel.IsProgressIndeterminate = false;
                _logService.LogError(ex, "Unexpected error during hash guessing.");
                _viewModel.StatusText = "Error during hash guessing execution.";
                _messageBoxService.ShowError("Hash Guessing Lab", ex.Message, Window.GetWindow(this));
            }
            finally
            {
                _viewModel.IsRunning = false;
                _viewModel.IsProgressIndeterminate = false;
                _cancellationTokenSource = null;
                if (_progressUIManager != null)
                {
                    await _progressUIManager.OnHashGuessingCompletedAsync();
                }
            }
        }

        private async Task RunInternalAsync(InternalHashAction action, IReadOnlySet<string> selectedSubMethods = null)
        {
            if (_viewModel.IsRunning) return;

            string rootPath = _appSettings.LolPbeDirectory?.Trim();
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                _messageBoxService.ShowError("Hash Guessing Lab", "Please configure the LoL PBE Install Directory in Settings first.", Window.GetWindow(this));
                return;
            }

            int domainIndex = DomainSelector.SelectedIndex;
            bool includeBin = domainIndex == 2;
            bool includeRst = domainIndex == 3;
            string domainName = includeBin ? "BIN" : "RST";

            var taskToken = _taskCancellationManager != null ? _taskCancellationManager.PrepareNewOperation() : CancellationToken.None;
            var runCancellation = new CancellationTokenSource();
            _cancellationTokenSource = runCancellation;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(runCancellation.Token, taskToken);
            var effectiveToken = linkedCts.Token;

            _viewModel.IsRunning = true;
            _viewModel.ProgressValue = 0;
            _viewModel.ProgressText = "Running";
            _viewModel.IsProgressIndeterminate = true;
            _viewModel.StatusText = $"Executing {domainName} {action}...";

            _viewModel.Matches.Clear();
            var displayedMatchHashes = new HashSet<ulong>();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            _progressUIManager?.OnHashGuessingStarted($"{domainName} {action}", "Preparing Hashes...");

            try
            {
                var progress = new Progress<InternalHashProgress>(p =>
                {
                    if (p.NewMatches != null && p.NewMatches.Count > 0)
                    {
                        foreach (var match in p.NewMatches)
                        {
                            if (displayedMatchHashes.Add(match.Hash))
                            {
                                _viewModel.Matches.Add(match);
                            }
                        }
                    }

                    if (p.TotalWads > 0)
                    {
                        _viewModel.IsProgressIndeterminate = false;
                        _viewModel.ProgressValue = p.ProcessedWads * 100d / p.TotalWads;
                        _viewModel.ProgressText = $"{_viewModel.ProgressValue:F0}%";
                    }
                    else if (p.CheckedCandidates > 0)
                    {
                        _viewModel.IsProgressIndeterminate = false;
                        _viewModel.ProgressText = $"{p.CheckedCandidates:N0} checked";
                    }
                    else
                    {
                        _viewModel.IsProgressIndeterminate = true;
                    }

                    string timeText = FormatElapsedTime(stopwatch.Elapsed);
                    _viewModel.StatusText = $"{p.CurrentStage} · {p.FoundMatches:N0} found · Time: {timeText}";

                    if (p.TotalWads > 0)
                    {
                        string stage = string.IsNullOrEmpty(p.CurrentStage) ? "WAD" : p.CurrentStage;
                        string statusMsg = $"Scanning {p.ProcessedWads} of {p.TotalWads} WADs: {stage}";
                        _progressUIManager?.OnHashGuessingProgressChanged(statusMsg, p.ProcessedWads, p.TotalWads, statusMsg, null);
                    }
                    else
                    {
                        string statusMsg = $"Hash Lab: {domainName} {action} · {p.FoundMatches:N0} found";
                        string customProgressText = p.CheckedCandidates > 0 ? $"{p.CheckedCandidates:N0} checked" : null;
                        _progressUIManager?.OnHashGuessingProgressChanged(statusMsg, 0, 0, $"{domainName} {action} · {p.FoundMatches:N0} found", customProgressText);
                    }
                });

                if (action == InternalHashAction.Inventory)
                {
                    var inv = await _binRstHashGuessingService.BuildInventoryAsync(rootPath, includeBin, includeRst, progress, effectiveToken);
                    stopwatch.Stop();
                    string elapsedTime = FormatElapsedTime(stopwatch.Elapsed);
                    _viewModel.ProgressValue = 100;
                    _viewModel.ProgressText = "100%";
                    _viewModel.IsProgressIndeterminate = false;
                    _viewModel.StatusText = $"Completed {domainName} inventory in {elapsedTime}: {inv.ScannedBins} BIN / {inv.ScannedStringTables} RST parsed.";
                }
                else
                {
                    InternalHashRunResult result = action switch
                    {
                        InternalHashAction.Content => await _binRstHashGuessingService.RunContentGuessingAsync(rootPath, includeBin, includeRst, progress, effectiveToken, selectedSubMethods: selectedSubMethods),
                        InternalHashAction.Structural => await _binRstHashGuessingService.RunStructuralGuessingAsync(rootPath, includeBin, includeRst, progress, effectiveToken, selectedSubMethods: selectedSubMethods),
                        _ => throw new ArgumentOutOfRangeException(nameof(action))
                    };

                    stopwatch.Stop();
                    string elapsedTime = FormatElapsedTime(stopwatch.Elapsed);
                    _viewModel.Matches.AddRange(result.Matches.Where(m => displayedMatchHashes.Add(m.Hash)));
                    _viewModel.ProgressValue = 100;
                    _viewModel.ProgressText = "100%";
                    _viewModel.IsProgressIndeterminate = false;
                    _viewModel.StatusText = $"Completed {domainName} {action} in {elapsedTime}: {result.Matches.Count:N0} matches discovered.";
                }

                UpdateUnknownCountAsync();
            }
            catch (OperationCanceledException)
            {
                _viewModel.ProgressText = "";
                _viewModel.ProgressValue = 0;
                _viewModel.IsProgressIndeterminate = false;
                _viewModel.StatusText = $"{domainName} operation cancelled.";
            }
            catch (Exception ex)
            {
                _viewModel.ProgressText = "";
                _viewModel.ProgressValue = 0;
                _viewModel.IsProgressIndeterminate = false;
                _logService.LogError(ex, $"Failed to execute {domainName} {action}.");
                _viewModel.StatusText = $"Error: {ex.Message}";
                _messageBoxService.ShowError("Hash Guessing Lab", ex.Message, Window.GetWindow(this));
            }
            finally
            {
                _viewModel.IsRunning = false;
                _viewModel.IsProgressIndeterminate = false;
                _cancellationTokenSource = null;
                if (_progressUIManager != null)
                {
                    await _progressUIManager.OnHashGuessingCompletedAsync();
                }
            }
        }

        private static string FormatElapsedTime(TimeSpan elapsed)
        {
            if (elapsed.TotalHours >= 1)
                return $"{elapsed.Hours}h {elapsed.Minutes:D2}m {elapsed.Seconds:D2}s";
            if (elapsed.TotalMinutes >= 1)
                return $"{elapsed.Minutes}m {elapsed.Seconds:D2}s";
            return $"{elapsed.TotalSeconds:F1}s";
        }

        private async Task RunScanUnknownsAsync(HashGuessDomain domain)
        {
            if (_viewModel.IsRunning) return;

            string rootPath = _appSettings.LolPbeDirectory?.Trim();
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                _messageBoxService.ShowError("Hash Guessing Lab", "Please configure the LoL PBE Install Directory in Settings first.", Window.GetWindow(this));
                return;
            }

            var taskToken = _taskCancellationManager != null ? _taskCancellationManager.PrepareNewOperation() : CancellationToken.None;
            var runCancellation = new CancellationTokenSource();
            _cancellationTokenSource = runCancellation;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(runCancellation.Token, taskToken);
            var effectiveToken = linkedCts.Token;

            _viewModel.IsRunning = true;
            _viewModel.ProgressValue = 0;
            _viewModel.ProgressText = "Scanning";
            _viewModel.IsProgressIndeterminate = true;
            _viewModel.StatusText = $"Scanning {domain} WADs for unknown chunks...";
            _progressUIManager?.OnHashGuessingStarted($"Scanning {domain} Unknowns", "Preparing WADs...");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var progress = new Progress<HashGuessProgress>(p =>
                {
                    if (p.TotalWads > 0)
                    {
                        _viewModel.IsProgressIndeterminate = false;
                        _viewModel.ProgressValue = p.ProcessedWads * 100d / p.TotalWads;
                        _viewModel.ProgressText = $"{_viewModel.ProgressValue:F0}%";
                        string timeText = FormatElapsedTime(stopwatch.Elapsed);
                        string statusMsg = $"Scanning {p.ProcessedWads} of {p.TotalWads} WADs: {p.CurrentWad}";
                        _viewModel.StatusText = $"{statusMsg} · Time: {timeText}";
                        _progressUIManager?.OnHashGuessingProgressChanged(statusMsg, p.ProcessedWads, p.TotalWads, statusMsg, null);
                    }
                });
                var summary = await _hashGuessingService.ScanUnknownHashesAsync(domain, rootPath, progress, effectiveToken);
                stopwatch.Stop();
                string elapsedTime = FormatElapsedTime(stopwatch.Elapsed);
                _viewModel.ProgressValue = 100;
                _viewModel.ProgressText = "100%";
                _viewModel.IsProgressIndeterminate = false;
                _viewModel.StatusText = $"Completed in {elapsedTime}: Found {summary.Total:N0} unknown hashes in scope ({summary.Current:N0} in current patch).";
                UpdateUnknownCountAsync();
            }
            catch (OperationCanceledException)
            {
                _viewModel.ProgressText = "";
                _viewModel.ProgressValue = 0;
                _viewModel.IsProgressIndeterminate = false;
                _viewModel.StatusText = "Scan cancelled.";
            }
            catch (Exception ex)
            {
                _viewModel.ProgressText = "";
                _viewModel.ProgressValue = 0;
                _viewModel.IsProgressIndeterminate = false;
                _logService.LogError(ex, "Failed to scan unknown hashes.");
                _viewModel.StatusText = $"Error: {ex.Message}";
                _messageBoxService.ShowError("Hash Guessing Lab", ex.Message, Window.GetWindow(this));
            }
            finally
            {
                _viewModel.IsRunning = false;
                _viewModel.IsProgressIndeterminate = false;
                _cancellationTokenSource = null;
                if (_progressUIManager != null)
                {
                    await _progressUIManager.OnHashGuessingCompletedAsync();
                }
            }
        }

        private enum HashGuessMode
        {
            GrepGame,
            GrepLcu,
            GamePrefixes,
            GameShaders,
            GameBasic,
            GameExtended,
            BannerGuess,
            GameCustom,
            LcuBasic,
            LcuExtended,
            LcuCustom,
            LcuScoped,
            LcuModifiers,
            LcuMedia,
            LcuV1Paths
        }

        private enum InternalHashAction
        {
            Inventory,
            Content,
            Structural
        }
    }
}
