﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Collections;
using ArchiSteamFarm.Localization;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Interaction;
using JetBrains.Annotations;
using Maxisoft.ASF.Configurations;
using Maxisoft.ASF.Reddit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamKit2;
using static ArchiSteamFarm.Core.ASF;

namespace Maxisoft.ASF;

#pragma warning disable CA1812 // ASF uses this class during runtime
[UsedImplicitly]
[SuppressMessage("Design", "CA1001:Disposable fields")]
internal sealed class ASFFreeGamesPlugin : IASF, IBot, IBotConnection, IBotCommand2, IUpdateAware {
	private const int CollectGamesTimeout = 3 * 60 * 1000;
	private const int DayInSeconds = 24 * 60 * 60;
	public string Name => nameof(ASFFreeGamesPlugin);
	public Version Version => typeof(ASFFreeGamesPlugin).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	private readonly ConcurrentHashSet<Bot> Bots = new(new BotEqualityComparer());
	private readonly ConcurrentDictionary<string, BotContext> BotContexts = new();
	private readonly RedditHelper RedditHelper = new();
	private SemaphoreSlim? SemaphoreSlim;
	private readonly object LockObject = new();
	private readonly Lazy<CancellationTokenSource> CancellationTS = new(static () => new CancellationTokenSource());
	private readonly HashSet<GameIdentifier> PreviouslySeenAppIds = new();
	private static readonly EPurchaseResultDetail[] InvalidAppPurchaseCodes = { EPurchaseResultDetail.AlreadyPurchased, EPurchaseResultDetail.RegionNotSupported, EPurchaseResultDetail.InvalidPackage, EPurchaseResultDetail.DoesNotOwnRequiredApp };
	private static readonly Lazy<Regex> InvalidAppPurchaseRegex = new(BuildInvalidAppPurchaseRegex);
	private readonly LoggerFilter LoggerFilter = new();
	private ASFFreeGamesOptions _options = new();

	// ReSharper disable once RedundantDefaultMemberInitializer
#pragma warning disable CA1805
	internal bool VerboseLog =>
#if DEBUG
		_options.VerboseLog ?? true
#else
		_options.VerboseLog ?? false
#endif
	;
#pragma warning restore CA1805

	private Timer? Timer;

	private enum CollectGameRequestSource {
		None = 0,
		RequestedByUser = 1,
		Scheduled = 2,
	}

	public Task OnLoaded() {
		if (VerboseLog) {
			ArchiLogger.LogGenericInfo($"Loaded {Name}", nameof(OnLoaded));
		}

		return Task.CompletedTask;
	}

	public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		string formatBotResponse(string resp) {
			return bot?.Commands?.FormatBotResponse(resp) ?? Commands.FormatStaticResponse(resp);
		}

		if (args is { Length: > 0 } && (args[0]?.ToUpperInvariant() == "GETIP")) {
			var webBrowser = bot?.ArchiWebHandler?.WebBrowser ?? WebBrowser;

			if (webBrowser is null) {
				return formatBotResponse(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsInvalid, nameof(webBrowser)));
			}

			try {
				var result = await webBrowser.UrlGetToJsonObject<JToken>(new Uri("https://httpbin.org/ip")).ConfigureAwait(false);
				string origin = result?.Content?.Value<string>("origin") ?? "";

				if (!string.IsNullOrWhiteSpace(origin)) {
					return formatBotResponse(origin);
				}
			}
			catch (Exception e) when (e is JsonException or IOException) {
				return formatBotResponse(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsInvalid, e.Message));
			}
		}

		if (args is { Length: > 0 } && (args[0]?.ToUpperInvariant() == "FREEGAMES")) {
			if (args.Length >= 2) {
				switch (args[1].ToUpperInvariant()) {
					case "SET":
						switch (args[2].ToUpperInvariant()) {
							case "VERBOSE":
								_options.VerboseLog = true;
								await SaveOptions().ConfigureAwait(false);

								return formatBotResponse("Verbosity on");
							case "NOVERBOSE":
								_options.VerboseLog = false;
								await SaveOptions().ConfigureAwait(false);

								return formatBotResponse("Verbosity off");
							case "F2P":
							case "FREETOPLAY":
							case "NOSKIPFREETOPLAY":
								_options.SkipFreeToPlay = false;
								await SaveOptions().ConfigureAwait(false);

								return formatBotResponse($"{Name} is going to collect f2p games");
							case "NOF2P":
							case "NOFREETOPLAY":
							case "SKIPFREETOPLAY":
								_options.SkipFreeToPlay = true;
								await SaveOptions().ConfigureAwait(false);

								return formatBotResponse($"{Name} is now skipping f2p games");
							case "DLC":
							case "NOSKIPDLC":
								_options.SkipDLC = false;
								await SaveOptions().ConfigureAwait(false);

								return formatBotResponse($"{Name} is going to collect dlc");
							case "NODLC":
							case "SKIPDLC":
								_options.SkipDLC = true;
								await SaveOptions().ConfigureAwait(false);

								return formatBotResponse($"{Name} is now skipping dlc");

							default:
								return formatBotResponse($"Unknown \"{args[2]}\" variable to set");
						}

					case "RELOAD":
						ASFFreeGamesOptionsLoader.Bind(ref _options);

						break;
				}
			}

			int collected = await CollectGames(CollectGameRequestSource.RequestedByUser, CancellationTS.Value.Token).ConfigureAwait(false);

			return formatBotResponse($"Collected a total of {collected} free game(s)");
		}

		return null;
	}

	private async Task SaveOptions() {
		using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationTS.Value.Token);
		cts.CancelAfter(10_000);
		await ASFFreeGamesOptionsLoader.Save(_options, cts.Token).ConfigureAwait(false);
	}

	public async Task OnASFInit(IReadOnlyDictionary<string, JToken>? additionalConfigProperties = null) {
		ASFFreeGamesOptionsLoader.Bind(ref _options);
		_options.VerboseLog ??= GlobalDatabase?.LoadFromJsonStorage($"{Name}.Verbose")?.ToObject<bool?>() ?? _options.VerboseLog;
		await SaveOptions().ConfigureAwait(false);
	}

	public async Task OnBotDestroy(Bot bot) => await RemoveBot(bot).ConfigureAwait(false);

	public Task OnBotInit(Bot bot) => Task.CompletedTask;

	public async Task OnBotDisconnected(Bot bot, EResult reason) => await RemoveBot(bot).ConfigureAwait(false);

	private void ResetTimer(Timer? newTimer = null) {
		Timer?.Dispose();
		Timer = newTimer;
	}

	private async Task RemoveBot(Bot bot) {
		Bots.Remove(bot);

		if (BotContexts.TryRemove(bot.BotName, out var ctx)) {
			await ctx.SaveToFileSystem().ConfigureAwait(false);
			ctx.Dispose();
		}

		if ((Bots.Count == 0)) {
			ResetTimer();
		}

		LoggerFilter.RemoveFilters(bot);
	}

	private async Task RegisterBot(Bot bot) {
		Bots.Add(bot);

		StartTimerIfNeeded();

		if (!BotContexts.TryGetValue(bot.BotName, out var ctx)) {
			lock (BotContexts) {
				if (!BotContexts.TryGetValue(bot.BotName, out ctx)) {
					ctx = BotContexts[bot.BotName] = new BotContext(bot);
				}
			}
		}

		await ctx.LoadFromFileSystem(CancellationTS.Value.Token).ConfigureAwait(false);
	}

	private void StartTimerIfNeeded() {
		if (Timer is null) {
			TimeSpan delay = TimeSpan.FromMilliseconds(_options.RecheckIntervalMs);
			ResetTimer(new Timer(CollectGamesOnClock));
			Timer?.Change(TimeSpan.FromSeconds(30), delay);
		}
	}

	public async Task OnBotLoggedOn(Bot bot) => await RegisterBot(bot).ConfigureAwait(false);

	private async void CollectGamesOnClock(object? source) {
		using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(CollectGamesTimeout));

		Bot[] reorderedBots;

		lock (BotContexts) {
			long orderByRunKeySelector(Bot bot) => BotContexts.TryGetValue(bot.BotName, out var ctx) ? ctx.RunElapsedMilli : long.MaxValue;
			int comparison(Bot x, Bot y) => orderByRunKeySelector(y).CompareTo(orderByRunKeySelector(x)); // sort in descending order
			reorderedBots = Bots.ToArray();
			Array.Sort(reorderedBots, comparison);
		}

		await CollectGames(reorderedBots, CollectGameRequestSource.Scheduled, cts.Token).ConfigureAwait(false);
	}

	private Task<int> CollectGames(CollectGameRequestSource requestSource, CancellationToken cancellationToken = default) => CollectGames(Bots, requestSource, cancellationToken);

	private async Task<int> CollectGames(IEnumerable<Bot> bots, CollectGameRequestSource requestSource, CancellationToken cancellationToken = default) {
		if (cancellationToken.IsCancellationRequested) {
			return 0;
		}

		SemaphoreSlim? semaphore = SemaphoreSlim;

		if (semaphore is null) {
			lock (LockObject) {
				SemaphoreSlim ??= new SemaphoreSlim(1, 1);
				semaphore = SemaphoreSlim;
			}
		}

		if (!await semaphore.WaitAsync(100, cancellationToken).ConfigureAwait(false)) {
			return 0;
		}

		int res = 0;

		try {
			ICollection<RedditGameEntry> games = await RedditHelper.ListGames().ConfigureAwait(false);

			LogNewGameCount(games, VerboseLog || requestSource is CollectGameRequestSource.RequestedByUser);

			foreach (Bot bot in bots) {
				if (cancellationToken.IsCancellationRequested) {
					break;
				}

				if (!bot.IsConnectedAndLoggedOn) {
					continue;
				}

				if (bot.GamesToRedeemInBackgroundCount > 0) {
					continue;
				}

				if (_options.IsBlacklisted(bot)) {
					continue;
				}

				bool save = false;
				BotContext context = BotContexts[bot.BotName];

				foreach ((string? identifier, long time, bool freeToPlay, bool dlc) in games) {
					if (freeToPlay && _options.SkipFreeToPlay is true) {
						continue;
					}

					if (dlc && _options.SkipDLC is true) {
						continue;
					}

					if (identifier is null || !GameIdentifier.TryParse(identifier, out var gid)) {
						continue;
					}

					if (context.HasApp(in gid)) {
						continue;
					}

					if (_options.IsBlacklisted(in gid)) {
						continue;
					}

					string? resp;

					string cmd = $"ADDLICENSE {bot.BotName} {gid}";

					if (VerboseLog) {
						bot.ArchiLogger.LogGenericDebug($"Trying to perform command \"{cmd}\"", nameof(CollectGames));
					}

					using (LoggerFilter.DisableLoggingForAddLicenseCommonErrors(_ => !VerboseLog && (requestSource is not CollectGameRequestSource.RequestedByUser) && context.ShouldHideErrorLogForApp(in gid), bot)) {
						resp = await bot.Commands.Response(EAccess.Operator, cmd).ConfigureAwait(false);
					}

					bool success = false;

					if (!string.IsNullOrWhiteSpace(resp)) {
						success = resp!.Contains("collected game", StringComparison.InvariantCultureIgnoreCase);
						success |= resp!.Contains("OK", StringComparison.InvariantCultureIgnoreCase);

						if (success || VerboseLog || requestSource is CollectGameRequestSource.RequestedByUser || !context.ShouldHideErrorLogForApp(in gid)) {
							bot.ArchiLogger.LogGenericInfo($"[FreeGames] {resp}", nameof(CollectGames));
						}
					}

					if (success) {
						lock (context) {
							context.RegisterApp(in gid);
						}

						save = true;
						res++;
					}
					else {
						if ((requestSource != CollectGameRequestSource.RequestedByUser) && (resp?.Contains("RateLimited", StringComparison.InvariantCultureIgnoreCase) ?? false)) {
							if (VerboseLog) {
								bot.ArchiLogger.LogGenericWarning("[FreeGames] Rate limit reached ! Skipping remaining games...", nameof(CollectGames));
							}

							break;
						}

						if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - time > DayInSeconds) {
							lock (context) {
								context.AppTickCount(in gid, increment: true);
							}
						}

						if (InvalidAppPurchaseRegex.Value.IsMatch(resp ?? "")) {
							save |= context.RegisterInvalidApp(in gid);
						}
					}
				}

				if (save) {
					await context.SaveToFileSystem(cancellationToken).ConfigureAwait(false);
				}

				context.NewRun();
			}
		}
		catch (TaskCanceledException) { }
		finally {
			semaphore.Release();
		}

		return res;
	}

	private void LogNewGameCount(ICollection<RedditGameEntry> games, bool logZero = false) {
		int totalAppIdCounter = PreviouslySeenAppIds.Count;
		int newGameCounter = 0;

		foreach (RedditGameEntry entry in games) {
			if (GameIdentifier.TryParse(entry.Identifier, out GameIdentifier identifier) && PreviouslySeenAppIds.Add(identifier)) {
				newGameCounter++;
			}
		}

		if ((totalAppIdCounter == 0) && (games.Count > 0)) {
			ArchiLogger.LogGenericInfo($"[FreeGames] found potentially {games.Count} free games on reddit", nameof(CollectGames));
		}
		else if (newGameCounter > 0) {
			ArchiLogger.LogGenericInfo($"[FreeGames] found {newGameCounter} fresh free game(s) on reddit", nameof(CollectGames));
		}
		else if ((newGameCounter == 0) && logZero) {
			ArchiLogger.LogGenericInfo($"[FreeGames] found 0 new game out of {games.Count} free games on reddit", nameof(CollectGames));
		}
	}

	private static Regex BuildInvalidAppPurchaseRegex() {
		StringBuilder stringBuilder = new("^.*?(?:");

		foreach (EPurchaseResultDetail code in InvalidAppPurchaseCodes) {
			stringBuilder.Append("(?:");
			ReadOnlySpan<char> codeString = code.ToString().Replace(nameof(EPurchaseResultDetail), @"\w*?", StringComparison.InvariantCultureIgnoreCase);

			while ((codeString.Length > 0) && (codeString[0] == '.')) {
				codeString = codeString[1..];
			}

			if (codeString.Length <= 1) {
				continue;
			}

			stringBuilder.Append(codeString[0]);

			foreach (char c in codeString[1..]) {
				if (char.IsUpper(c)) {
					stringBuilder.Append(@"(?>\s*)");
				}

				stringBuilder.Append(c);
			}

			stringBuilder.Append(")|");
		}

		while ((stringBuilder.Length > 0) && (stringBuilder[^1] == '|')) {
			stringBuilder.Length -= 1;
		}

		stringBuilder.Append(").*?$");

		return new Regex(stringBuilder.ToString(), RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);
	}

	~ASFFreeGamesPlugin() {
		SemaphoreSlim?.Dispose();
		SemaphoreSlim = null;
		Timer?.Dispose();
		Timer = null;
	}

	public async Task OnUpdateFinished(Version currentVersion, Version newVersion) => await SaveOptions().ConfigureAwait(false);

	public Task OnUpdateProceeding(Version currentVersion, Version newVersion) => Task.CompletedTask;
}
#pragma warning restore CA1812 // ASF uses this class during runtime
