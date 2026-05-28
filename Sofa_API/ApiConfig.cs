using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sofa_API;

/// <summary>
/// Contains everything related to user configuration, versioning, and the filesystem environment.
/// </summary>
public static class ApiConfig
{
	/// <summary>
	/// String containing the semver-aligned version of the current Sofa instance.
	/// </summary>
	public const string Version = "0.19.28";
	/// <summary>
	/// Represents the current Sofa instance's MAJOR version in semver.
	/// </summary>
	public const byte ApiVersion = 0;

	/// <summary>
	/// The base path for API endpoints, including the API version. Prefixed before all endpoints
	/// </summary>
	public static readonly string BaseApiUrlPath = $"/api/v{ApiVersion}";
	/// <summary>
	/// HTTP network port to expose the API on. Tries to use the env var <c>SOFA_NETWORK_PORT</c> first, then falls back to 5899.
	/// </summary>
	/// <seealso cref="ApiConfig.HttpsNetworkPort"/>
	public static readonly ushort NetworkPort = (ushort) (ushort.TryParse(Environment.GetEnvironmentVariable("SOFA_NETWORK_PORT"), out var port) ? port : 5899);
	/// <summary>
	/// HTTPS network port to expose the API on. Tries to use the env var <c>SOFA_HTTPS_NETWORK_PORT</c> first, then falls back to 5988.
	/// </summary>
	/// <seealso cref="ApiConfig.NetworkPort"/>
	public static readonly ushort HttpsNetworkPort = (ushort) (ushort.TryParse(Environment.GetEnvironmentVariable("SOFA_HTTPS_NETWORK_PORT"), out var port) ? port : 5988);

	/// <summary>
	/// This Sofa instance's unique ID. Represented as a GUIDv7.
	/// </summary>
	/// <remarks>
	///	This is generated once, and stored in a file in the certificates folder. If the file is missing, a new one is generated.
	/// </remarks>
	public static Guid InstanceId
	{
		get
		{
			var idFilePath = Path.Combine(SofaPaths.SubPaths.PathToCertificatesFolder, "instanceId");
			if (field == Guid.Empty)
			{
				if (!File.Exists(idFilePath))
				{
					field = GenerateNewId();
				}
				else
				{
					field = Guid.TryParse(File.ReadAllBytes(idFilePath), out var parsedId)
						? parsedId
						: GenerateNewId();
				}
			}

			return field;

			// A local helper function inside a getter feels cursed :(
			// This just creates a new GUIDv7, and writes that to the file at idFilePath
			Guid GenerateNewId()
			{
				Logs.LogBook.Write(new(LogStream.Verbose, "Instance ID",
					"Couldn't find the instance ID file. Generating a new one."));
				var id = Guid.CreateVersion7();
				File.WriteAllBytes(idFilePath, id.ToByteArray());
				return id;
			}
		}
	} = Guid.Empty;

	/// <summary>
	/// A stopwatch used to track how long Sofa has been running for. Started on API startup
	/// </summary>
	public static readonly Stopwatch SofaStartStopwatch = Stopwatch.StartNew();

	/// <summary>
	/// Indicates how verbose the API's terminal output should be. 0-7, with 7 being the most verbose. Tries to use the env var <c>SOFA_VERBOSITY</c> first, then falls back to 6 (Up to NOTICE).
	/// </summary>
	/// <remarks>
	///	As is, this only affects the PRINTING of logs. Logs might still be written to the log file, depending on the <see cref="ApiConfig.ApiConfiguration.LogVerbosity"/>.
	/// </remarks>
	// If the Env Var contains anything, clamp it to the acceptable range of the logStream enum type
	public static readonly LogStream TerminalVerbosity =
			byte.TryParse(Environment.GetEnvironmentVariable("SOFA_VERBOSITY"), out var terminalVerbosity)
				? ParsingMethods.ClampToMaxLogStreamValue(terminalVerbosity)
				: LogStream.Notice;

	// Config-specific stuff from here on out


	/// <summary>
	/// Enum containing all the stats this API can fetch.
	/// </summary>
	public enum SystemStats : byte
	{
		Meta,
		System,
		Cpu,
		Gpu,
		Memory,
		Mounts,
		Batteries
	}

	// Config management

	public static readonly JsonSerializerOptions SofaJsonSerializerOptions = new()
	{
		WriteIndented = true,
		IndentCharacter = '\t',
		IndentSize = 1
	};

	/// <summary>
	/// A unified class to access and modify all the API's configuration properties.
	/// </summary>
	public static class ApiConfiguration
	{
		static ApiConfiguration()
		{
			LoadConfig();
		}

		/// <summary>
		/// The major API version associated with the current config.
		/// </summary>
		/// <remarks>
		///	This is required in the saved config.
		/// </remarks>
		public static byte ConfigVersion { get; private set; } = ApiVersion;
		/// <summary>
		/// The user-set name for this instance of Sofa.
		/// </summary>
		/// <remarks>
		///	Defaults to "Sofa Instance"
		/// </remarks>
		public static string BackendName { get; private set; } = "Sofa Instance";

		/// <summary>
		/// Lifetime of the cache. Set to 0 to effectively disable it.
		/// </summary>
		/// <remarks>
		///	Defaults to 5 seconds.
		/// </remarks>
		public static TimeSpan CacheLifetime { get; private set; } = TimeSpan.FromSeconds(5);
		/// <summary>
		/// Reflects the user's <see cref="CacheLifetime"/> clamped to a minimum of 3 seconds. Used for the initial fetch cycle as to not repeat it a few times.
		/// </summary>
		public static TimeSpan ClampedCacheLifetime => CacheLifetime.TotalSeconds < 3 ? TimeSpan.FromSeconds(3) : CacheLifetime;

		/// <summary>
		/// The maximum lifespan before a Client's request (either for a permission update or registration) is considered expired and auto-rejected.
		/// </summary>
		/// <remarks>
		///	Defaults to 30 minutes.
		/// </remarks>
		public static TimeSpan ClientRequestLifetime { get; private set; } = TimeSpan.FromMinutes(30);

		/// <summary>
		/// Whether the Docker integration is enabled.
		/// </summary>
		/// <remarks>
		///	Defaults to true.
		/// </remarks>
		public static bool DockerIntegrationEnabled { get; private set; } = true;

		/// <summary>
		/// The verbosity level of the written logs. Does not control the verbosity of the terminal output.
		/// </summary>
		/// <seealso cref="ApiConfig.TerminalVerbosity"/>
		public static LogStream LogVerbosity { get; private set; } = LogStream.Verbose;

		/// <summary>
		/// Dictionary of watched mounts. Format is { "Path": "Name" }. For example, { "/home": "Home Partition" }
		/// </summary>
		/// <remarks>
		///	Defaults to { "/": "Root Partition" }. This is required in the saved config.
		/// </remarks>
		public static Dictionary<string, string> WatchedMounts { get; private set; } = new() { { "/", "Root Partition" } };

		/// <summary>
		/// JSON form of the <see cref="WolClientsClass"/> property. It's recommended to use that instead.
		/// </summary>
		/// <remarks>
		///	This is required in the saved config.
		/// </remarks>
		public static Dictionary<string, Dictionary<string, string?>> WolClients { get; private set; } = [];
		/// <summary>
		/// List of <see cref="WolHandling.WolClient"/>s saved by the user.
		/// </summary>
		/// <remarks>
		///	Defaults to empty. Generated from <see cref="WolClients"/> during startup.
		/// </remarks>
		[JsonIgnore]
		public static List<WolHandling.WolClient>? WolClientsClass { get; private set; }

		// Methods

		/// <summary>
		/// Gets the live configs as a Dictionary. Useful for JSON conversion.
		/// </summary>
		/// <returns></returns>
		public static Dictionary<string, dynamic> ToDictionary()
		{
			return new()
			{
				{ nameof(ConfigVersion), ConfigVersion },
				{ nameof(BackendName), BackendName },
				{ nameof(CacheLifetime), CacheLifetime.TotalSeconds },
				{ nameof(ClientRequestLifetime), ClientRequestLifetime.TotalSeconds },
				{ nameof(DockerIntegrationEnabled), DockerIntegrationEnabled },
				{ nameof(LogVerbosity), LogVerbosity },
				{ nameof(WatchedMounts), WatchedMounts },
				{ nameof(WolClients), WolClients }
			};
		}
		/// <summary>
		/// Serialize and flush the live configs to disk.
		/// </summary>
		private static void SaveConfig()
		{
			File.WriteAllText(SofaPaths.SubPaths.ConfigFilePath, JsonSerializer.Serialize(ToDictionary(), SofaJsonSerializerOptions));
		}

		/// <summary>
		/// Checks the corresponding in-disk configuration file for corruption or incompleteness and loads it.
		/// </summary>
		public static void LoadConfig()
		{
			CheckConfig();

			var loadedDict = (JsonSerializer.Deserialize<JsonDocument>(File.ReadAllText(SofaPaths.SubPaths.ConfigFilePath, Encoding.UTF8))
			                 ?? throw new Exception("Failed to deserialize config file")).RootElement;

			// This needs some cleaning.
			ConfigVersion = loadedDict.GetProperty(nameof(ConfigVersion)).GetByte();
			BackendName = loadedDict.TryGetProperty(nameof(BackendName), out var backendName) ? backendName.GetString() ?? BackendName : BackendName;

			CacheLifetime = loadedDict.TryGetProperty(nameof(CacheLifetime), out var cacheLifetime) ? TimeSpan.FromSeconds(double.Clamp(cacheLifetime.GetInt32(), 0, double.MaxValue)) : CacheLifetime;
			ClientRequestLifetime = loadedDict.TryGetProperty(nameof(ClientRequestLifetime), out var clientRequestLifetime) ? TimeSpan.FromSeconds(double.Clamp(clientRequestLifetime.GetInt32(), 0, double.MaxValue)) : ClientRequestLifetime;

			DockerIntegrationEnabled = loadedDict.TryGetProperty(nameof(DockerIntegrationEnabled), out var dockerIntegrationEnabled) ? dockerIntegrationEnabled.GetBoolean() : DockerIntegrationEnabled;

			LogVerbosity = loadedDict.TryGetProperty(nameof(LogVerbosity), out var logVerbosity) ? ParsingMethods.ClampToMaxLogStreamValue((byte) int.Clamp(logVerbosity.GetInt32(), 0, byte.MaxValue)) : LogVerbosity;

			if (loadedDict.TryGetProperty(nameof(WatchedMounts), out var watchedMounts))
			{
				try
				{
					WatchedMounts = watchedMounts.Deserialize<Dictionary<string, string>>() ?? WatchedMounts;
				}
				catch (JsonException e)
				{
					// Technically could recover from this by regenerating the config, but I don't wanna reset the user's entire config every time they mess up their JSON.
					Logs.LogBook.Write(new(LogStream.Fatal, "Watched Mounts Load",
						"Failed to load a mount entry from the configuration file. Please ensure the JSON is valid."));
					Logs.LogBook.Write(new(LogStream.Fatal, "Watched Mounts Load", $"Error: {e.Message}\n\tStack trace: {e.StackTrace}"));
					throw new Exception();
				}
			}
			if (loadedDict.TryGetProperty(nameof(WolClients), out var wolClients))
			{
				try
				{
					WolClients = wolClients.Deserialize<Dictionary<string, Dictionary<string, string?>>>() ?? WolClients;
				}
				catch (JsonException e)
				{
					// Technically could recover from this by regenerating the config, but I don't wanna reset the user's entire config every time they mess up their JSON.
					Logs.LogBook.Write(new(LogStream.Fatal, "WoL Load",
						"Failed to load the list of WoL clients from the configuration file. Please ensure the JSON is valid."));
					Logs.LogBook.Write(new(LogStream.Fatal, "WoL Load", $"Error: {e.Message}\n\tStack trace: {e.StackTrace}"));
					throw new Exception();
				}
			}

			LoadWolClientsList();
		}

		// Config file maintainence

		/// <summary>
		/// Checks the in-disk configuration file. If it's missing, corrupt, or incomplete, it regenerates it with some defaults
		/// </summary>
		/// <remarks>
		/// The old (potentially corrupted) file is saved as a ".old" file alongside the current one, in case this function misdetects a valid file as invalid.
		/// </remarks>
		private static void CheckConfig()
		{
			if (!File.Exists(SofaPaths.SubPaths.ConfigFilePath))
			{
				Logs.LogBook.Write(new LogEntry(LogStream.Info, "Configuration", "Couldn't find the configuration file. A new one will be generated."));
				SaveConfig();
			}
			if (ValidateConfigSyntax()) return;


			if (File.Exists($"{SofaPaths.SubPaths.ConfigFilePath}.old"))
			{
				File.Delete($"{SofaPaths.SubPaths.ConfigFilePath}.old");
			}
			File.Move(SofaPaths.SubPaths.ConfigFilePath, $"{SofaPaths.SubPaths.ConfigFilePath}.old");
			SaveConfig();
		}

		/// <summary>
		/// Check the config file for any corruption and the existence of the required minimum properties.
		/// </summary>
		/// <returns>True if the file is valid. False otherwise.</returns>
		private static bool ValidateConfigSyntax()
		{
			try
			{
				var jsonDocument = JsonDocument.Parse(File.ReadAllText(SofaPaths.SubPaths.ConfigFilePath)).RootElement;

				if (jsonDocument.TryGetProperty(nameof(ConfigVersion), out var configVersion) && configVersion.ValueKind == JsonValueKind.Number && configVersion.GetByte() != ApiVersion)
				{
					Logs.LogBook.Write(new(LogStream.Warning, "Configuration", $"Loaded configuration file is version {configVersion.GetByte()}, but the current version is {ApiVersion}. Here be dragons."));
				}

				return jsonDocument.TryGetProperty(nameof(ConfigVersion), out configVersion) && configVersion.ValueKind == JsonValueKind.Number;
			}
			catch (JsonException exception)
			{
				Logs.LogBook.Write(new(LogStream.Error, "Configuration", $"Failed processing JSON File, error message: {exception.Message} at {exception.LineNumber}:{exception.BytePositionInLine}."));
				Logs.LogBook.Write(new(LogStream.Error, "Configuration", $"Your configuration will be regenerated. You can find your old configuration in {SofaPaths.SubPaths.ConfigFilePath}.old"));
				return false;
			}
		}

		// Altering/Accessing config files

		/// <summary>
		/// Provides edit access to the configuration, both live and in-disk.
		/// </summary>
		/// <remarks>
		///	Changes to the <see cref="WatchedMounts"/>, <see cref="WolClients"/>, <see cref="WolClientsClass"/>,
		/// or the <see cref="ConfigVersion"/> properties are ignored by this method. Use the appropriate methods for that.
		/// </remarks>
		/// <param name="newProps">
		///	Configs to edit. In the format: <c>{ "BackendName": "Test" }</c>
		/// </param>
		/// <seealso cref="AddMountpoint"/>
		/// <seealso cref="RemoveMountpoints"/>
		/// <seealso cref="AddWolClient"/>
		/// <seealso cref="RemoveWolClient"/>
		/// <returns>Whether the method actually changed any configs.</returns>
		public static bool EditConfig(Dictionary<string, dynamic> newProps)
		{
			Logs.LogBook.Write(new(LogStream.Verbose, "Configuration Edit", $"Configuration Edit Requested: {string.Join(", ", newProps.Keys)}"));
			if (newProps.Count == 0) return false;
			bool configChanged = false;

			foreach (var newPropKvp in newProps)
			{
				switch (newPropKvp.Key)
				{
					case nameof(BackendName) when newPropKvp.Value.GetString() is string newPropString && newPropString != BackendName:
					{
						BackendName = newPropString;
						configChanged = true;
						break;
					}
					case nameof(CacheLifetime) when double.Clamp(newPropKvp.Value.GetInt32(), 0, double.MaxValue) != CacheLifetime.TotalSeconds:
					{
						CacheLifetime = TimeSpan.FromSeconds(double.Clamp(newPropKvp.Value.GetInt32(), 0, double.MaxValue));
						configChanged = true;
						break;
					}
					case nameof(ClientRequestLifetime) when double.Clamp(newPropKvp.Value.GetInt32(), 0, double.MaxValue) != ClientRequestLifetime.TotalSeconds:
					{
						ClientRequestLifetime = TimeSpan.FromSeconds(double.Clamp(newPropKvp.Value.GetInt32(), 0, double.MaxValue));
						configChanged = true;
						break;
					}
					case nameof(DockerIntegrationEnabled) when newPropKvp.Value.GetBoolean() != DockerIntegrationEnabled:
					{
						DockerIntegrationEnabled = newPropKvp.Value.GetBoolean();
						configChanged = true;
						break;
					}
					case nameof(LogVerbosity) when newPropKvp.Value.GetByte() != LogVerbosity:
					{
						LogVerbosity = newPropKvp.Value.GetByte();
						configChanged = true;
						break;
					}
				}
			}

			if (configChanged) SaveConfig();
			return configChanged;
		}

		// Mountpoint management

		/// <summary>
		/// Adds however many mountpoints you'd like to the in-disk configuration and updates the live configuration.
		/// </summary>
		/// <param name="mountPoints">
		///	Dictionary of mountpoints to add.
		/// Keys being the mountpoint path, and values being the user's label for each.
		/// The label (value) can be null, but it'll default to the name of "Mount".
		/// </param>
		/// <returns>True if the config was changed. False otherwise.</returns>
		public static bool AddMountpoint(Dictionary<string, string?> mountPoints)
		{
			if (mountPoints.Count == 0) return false;

			bool configChanged = false;
			foreach (var mountPointToAdd in mountPoints.Where(mountPointToAdd => !WatchedMounts.ContainsKey(mountPointToAdd.Key)))
			{
				WatchedMounts.Add(mountPointToAdd.Key, mountPointToAdd.Value ?? "Mount");
				StatHandlers.DiskHandling.FullDisksData.AddMount(mountPointToAdd.Key, mountPointToAdd.Value ?? "Mount");
				configChanged = true;
			}

			if (!configChanged) return false;
			SaveConfig();
			return true;
		}


		/// <summary>
		/// Remove a list of mountpoints from the configuration, both live and in-disk.
		/// </summary>
		/// <param name="mountPoints">The list of mountpoint's paths (Dict keys) to remove</param>
		public static void RemoveMountpoints(List<string> mountPoints)
		{
			if (mountPoints.Count == 0) return;

			foreach (var mountPoint in mountPoints)
			{
				WatchedMounts.Remove(mountPoint);
				StatHandlers.DiskHandling.FullDisksData.RemoveMount(mountPoint);
			}

			SaveConfig();
		}

		// WOL management

		/// <summary>
		/// Generates and sets the appropriate <see cref="WolClientsClass"/> derived from the <see cref="WolClients"/> property.
		/// </summary>
		private static void LoadWolClientsList()
		{
			List<WolHandling.WolClient> wolClientsList = [];

			foreach (var wolClientDict in WolClients)
			{
				try
				{
					IPAddress? broadcastAddress = null;
					if (wolClientDict.Value.TryGetValue("BroadcastAddress", out var rawBroadcastAddress) && rawBroadcastAddress != "null" && rawBroadcastAddress != null)
					{
						broadcastAddress = IPAddress.Parse(rawBroadcastAddress);
					}

					wolClientsList.Add(new WolHandling.WolClient
					{
						Name = wolClientDict.Value.GetValueOrDefault("Name"),
						PhysicalAddress = PhysicalAddress.Parse(wolClientDict.Key),
						IpAddress = IPAddress.Parse(wolClientDict.Value["IpAddress"]!),
						SubnetMask = IPAddress.Parse(wolClientDict.Value["SubnetMask"]!),
						BroadcastAddress = broadcastAddress
					});
				}
				catch (Exception e)
				{
					var name = wolClientDict.Value.GetValueOrDefault("Name");
					Logs.LogBook.Write(new(LogStream.Error, "WoL Init",
						$"Got a '{e.Message}' error trying to load a WoL client from the configuration file. Detected name: {name ?? "(unable to fetch name)"} Skipping."));
				}
			}

			WolClientsClass = wolClientsList;
		}

		/// <summary>
		/// Append a WoL client to the configuration. Updates the live and in-disk configuration.
		/// </summary>
		/// <param name="clientAddress">The IP Address of the client to add.</param>
		/// <param name="clientLabel">The label for the client to add.</param>
		/// <returns>True if the client was added. False if something went wrong.</returns>
		public static bool AddWolClient(string clientAddress, string clientLabel)
		{
			ShellResult physicalAddressProcess;
			ShellResult subnetMaskProcess;
			try
			{
				physicalAddressProcess = ShellMethods.RunShell($"{SofaPaths.BaseExecutablePath}/scripts/getNet.sh",
					["PhysicalAddress", clientAddress]).Result;

				subnetMaskProcess = ShellMethods.RunShell($"{SofaPaths.BaseExecutablePath}/scripts/getNet.sh", ["Netmask"]).Result;
			}
			catch (TimeoutException)
			{
				Logs.LogBook.Write(new(LogStream.Error, "WoL Client Add", $"getNet.sh timed out while fetching the client's physical address or subnet mask ({clientAddress}). Skipping."));
				return false;
			}

			if (!subnetMaskProcess.IsSuccess || !IPAddress.TryParse(subnetMaskProcess.StandardOutput, out var subnetMask)
			   || !physicalAddressProcess.IsSuccess || !PhysicalAddress.TryParse(physicalAddressProcess.StandardOutput, out var physicalAddress))
			{
				return false;
			}

			WolClients.TryAdd(physicalAddress.ToString(), new()
			{
				{ "Name", clientLabel },
				{ "IpAddress", clientAddress },
				{ "SubnetMask", subnetMask.ToString() },
				{ "BroadcastAddress", null }
			});
			WolClientsClass?.Add(new WolHandling.WolClient
			{
				Name = clientLabel,
				PhysicalAddress = physicalAddress,
				IpAddress = IPAddress.Parse(clientAddress),
				SubnetMask = subnetMask,
				BroadcastAddress = null
			});

			SaveConfig();
			return true;
		}

		/// <summary>
		/// Remove a specific client from the current configuration. Updates the live and in-disk configuration.
		/// </summary>
		/// <param name="clientAddress">Local IP Address of the client to remove.</param>
		/// <returns>True if the client was removed, false if the element was not found, and null if something went wrong.</returns>
		public static bool? RemoveWolClient(IPAddress clientAddress)
		{
			string physicalAddressStdout;
			try
			{
				physicalAddressStdout = ShellMethods.RunShell($"{SofaPaths.BaseExecutablePath}/scripts/getNet.sh",
					["PhysicalAddress", clientAddress.ToString()]).Result.StandardOutput;
			}
			catch (TimeoutException)
			{
				Logs.LogBook.Write(new(LogStream.Error, "WoL Client Remove", $"getNet.sh timed out while ({clientAddress.ToString()}). Skipping."));
				return null;
			}

			if (!PhysicalAddress.TryParse(physicalAddressStdout, out var physicalAddress))
			{
				Logs.LogBook.Write(new(LogStream.Error, "WoL Client Remove", $"getNet.sh output seems to be malformed or incorrect for {clientAddress}. Skipping."));
				return null;
			}

			if (!WolClients.Remove(physicalAddress.ToString())) return false;
			LoadWolClientsList();
			SaveConfig();
			return true;
		}

		/// <summary>
		/// Update or "fill in" the broadcast address of a specific WolClient and reload the live and in-disk configuration.
		/// </summary>
		/// <param name="wolClient">WolClient object</param>
		/// <param name="newBroadcastAddress">The broadcast address to fill in</param>
		internal static void UpdateBroadcastAddress(WolHandling.WolClient wolClient, string newBroadcastAddress)
		{
			WolClients[wolClient.PhysicalAddress.ToString()]["BroadcastAddress"] = newBroadcastAddress;

			SaveConfig();
			LoadWolClientsList();
		}
	}
}