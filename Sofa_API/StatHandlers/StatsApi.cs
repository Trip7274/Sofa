using System.Net;
using System.Net.Sockets;

namespace Sofa_API.StatHandlers;

/// <summary>
/// Contains methods and classes for storing and fetching general system info, along with CPU and RAM stats.
/// </summary>
public static class StatsApi
{

	/// <summary>
	/// General specs include generally static and general info about the system, or info about the system that's not inherent to Sofa.
	/// Some of these *are* dynamic, but keep in mind that the constructor only runs on start-up.
	/// </summary>
	public static class GeneralSpecs
	{
		static GeneralSpecs()
		{
			HostName = Environment.MachineName;
			DistroName = ShellMethods
				.RunShell($"{SofaPaths.BaseExecutablePath}/scripts/getSys.sh", ["Distro.Name"]).Result.StandardOutput;
			KernelName = ShellMethods.RunShell("uname", ["-s"]).Result.StandardOutput;
			KernelVersion = ShellMethods.RunShell("uname", ["-r"]).Result.StandardOutput;
			KernelArch = ShellMethods.RunShell("uname", ["-m"]).Result.StandardOutput;

			// Recieve hex codes from shell scripts
			var unprocessedDistroBrandColor = ShellMethods.RunShell($"{SofaPaths.BaseExecutablePath}/scripts/getSys.sh", ["Distro.Color"]).Result.StandardOutput;

			if (!string.IsNullOrWhiteSpace(unprocessedDistroBrandColor) && unprocessedDistroBrandColor != "null")
			{
				DistroBrandColor = unprocessedDistroBrandColor;
			}
		}

		/// <summary>
		/// The system's hostname.
		/// </summary>
		public static string HostName { get; }
		/// <summary>
		/// The system's friendly distro name.
		/// </summary>
		public static string DistroName { get; }
		/// <summary>
		/// Name of the current kernel. Usually "Linux"
		/// </summary>
		public static string KernelName { get; }
		/// <summary>
		/// Version of the current kernel.
		/// </summary>
		public static string KernelVersion { get; }
		/// <summary>
		/// Architecture of the current kernel.
		/// </summary>
		public static string KernelArch { get; }
		/// <summary>
		/// The user distro's colors, if any were present in /etc/os-release.
		/// </summary>
		public static string? DistroBrandColor { get; }


		/// <summary>
		/// The current system's uptime.
		/// </summary>
		public static TimeSpan SystemUptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

		public static Dictionary<string, dynamic?> ToDictionary()
		{
			return new Dictionary<string, dynamic?>
			{
				{ nameof(HostName), HostName },
				{ nameof(DistroName), DistroName },
				{ nameof(KernelName), KernelName },
				{ nameof(KernelVersion), KernelVersion },
				{ nameof(KernelArch), KernelArch },
				{ nameof(DistroBrandColor), DistroBrandColor },
				{ nameof(DockerLocal.IsDockerAvailable), DockerLocal.IsDockerAvailable },
				{ nameof(DockerLocal.IsDockerComposeAvailable), DockerLocal.IsDockerComposeAvailable },
				{ nameof(SystemUptime), SystemUptime }
			};
		}
	}

	/// <summary>
	/// Contains data about the system's current CPU. Make sure this is updated using <see cref="CpuData.UpdateData"/>
	/// </summary>
	public static class CpuData
	{
		static CpuData()
		{
			var rawOutput = ShellMethods.RunShell($"{SofaPaths.BaseExecutablePath}/scripts/getCpu.sh", ["Name"]).Result;
			if (!rawOutput.IsSuccess)
			{
				throw new Exception($"Failed to get CPU name from getCpu.sh ({rawOutput.ExitCode}).");
			}
			// "(R)" -> "®" (Common in Intel CPU names)
			// "(TM)" -> "™" (Common in Intel CPU names)
			Name = rawOutput.StandardOutput
				.Replace("(R)", "\u00AE")
				.Replace("(TM)", "\u2122");
		}

		/// <summary>
		/// The CPU's user-facing name. E.g., "AMD Ryzen 5 7600X 6-Core Processor"
		/// </summary>
		public static string? Name { get; }
		/// <summary>
		/// The average percentage of CPU utilization across all cores.
		/// </summary>
		public static float UtilizationPerc { get; private set; }

		/// <summary>
		/// The number of *physical* cores on the CPU
		/// </summary>
		public static ushort PhysicalCoreCount { get; private set; }
		/// <summary>
		/// The number of logical cores on the CPU. AKA, CPU threads
		/// </summary>
		public static ushort ThreadCount { get; private set; }

		public static int AverageFrequencyMHz { get; private set; }

		/// <summary>
		/// The average CPU's temperature. Either the "Package id 0" on Intel or "Tctl" on AMD.
		/// </summary>
		public static float? TemperatureC { get; private set; }
		/// <summary>
		/// The source of the temperature reading. Used only internally to set <see cref="TemperatureC"/> more percisely.
		/// </summary>
		private static string? TemperatureType { get; set; }

		/// <summary>
		/// The last time this object was updated.
		/// </summary>
		public static DateTime LastUpdate { get; private set; } = DateTime.MinValue;
		/// <summary>
		/// Returns whether the current data is too stale and should be updated.
		/// </summary>
		public static bool ShouldUpdate => LastUpdate + ApiConfig.ApiConfiguration.CacheLifetime < DateTime.Now;

		private static Task? UpdatingTask { get; set; }
		private static readonly Lock UpdatingLock = new();

		/// <summary>
		/// Check if the object's data is stale, if so, update it using <see cref="UpdateData"/>.
		/// </summary>
		public static async Task UpdateDataIfNecessary()
		{
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "CPU Fetch", "Checking for CPU data update..."));
			if (!ShouldUpdate)
			{
				Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "CPU Fetch", "CPU data is up to date."));
				return;
			}
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "CPU Fetch", "Updating CPU data..."));

			var localTask = UpdatingTask;
			if (localTask is null)
			{
				lock (UpdatingLock)
				{
					UpdatingTask ??= UpdateData();
					localTask = UpdatingTask;
				}
			}

			await localTask;
			lock (UpdatingLock)
			{
				UpdatingTask = null;
			}
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "CPU Fetch", "CPU data updated."));
		}

		/// <summary>
		/// Updates the cached CPU data stored in this CpuData class with the latest metrics.
		/// Make sure to invoke this as to not serve stale data.
		/// </summary>
		/// <exception cref="Exception">Non-zero shell script exit code, or the output was of invalid length.</exception>
		public static async Task UpdateData()
		{
			var shellTimeout = TimeSpan.FromMilliseconds(2500);
			if (LastUpdate == DateTime.MinValue)
			{
				shellTimeout *= 10;
			}
			var frequencyUpdateTask = Task.Run(FetchFrequency);
			var rawOutput = await ShellMethods.RunShell($"{SofaPaths.BaseExecutablePath}/scripts/getCpu.sh", ["AllUtil"], shellTimeout);
			if (!rawOutput.IsSuccess)
			{
				throw new Exception($"Failed to get CPU data from getCpu.sh ({rawOutput.ExitCode}).");
			}

			string[] outputArray = rawOutput.StandardOutput.Split('|');

			if (outputArray.Length != 5)
			{
				throw new Exception($"Invalid output from getCpu.sh (Expected 5 entries, got {outputArray.Length}).");
			}

			var temp = outputArray[4].ParseNullable<float>();
			if (outputArray[3] == "thermalZone")
			{
				temp /= 1000;
			}
			if (outputArray[3] != "null")
			{
				temp = MathF.Round(temp ?? 0, 2);
			}

			UtilizationPerc = MathF.Round(float.Parse(outputArray[0]), 2);
			PhysicalCoreCount = ushort.Parse(outputArray[1]);
			ThreadCount = ushort.Parse(outputArray[2]);
			TemperatureType = outputArray[3] == "null" ? null : outputArray[3];
			TemperatureC = temp;
			AverageFrequencyMHz = await frequencyUpdateTask;
			LastUpdate = DateTime.Now;
		}

		public static async Task<int> FetchFrequency()
		{
			const string basePath = "/sys/devices/system/cpu/";
			List<int> coreFrequencies = new(ThreadCount);

			foreach (var cpuDirectory in Directory.EnumerateDirectories(basePath, "cpu*"))
			{
				string cpuFreqFile = Path.Combine(cpuDirectory, "cpufreq", "scaling_cur_freq");
				if (!File.Exists(cpuFreqFile)) continue;

				coreFrequencies.Add(int.Parse(await File.ReadAllTextAsync(cpuFreqFile)));
			}

			return coreFrequencies.Count switch
			{
				0 => 0,
				1 => coreFrequencies[0],
				_ => (int) Math.Round(coreFrequencies.Average() / 1000F)
			};
		}

		/// <summary>
		/// Get this object in the form of a Dictionary. Used for forming responses.
		/// </summary>
		/// <returns>This object represented as a Dictionary.</returns>
		public static Dictionary<string, dynamic?> ToDictionary()
		{
			return new Dictionary<string, dynamic?>
			{
				{ nameof(Name), Name },
				{ nameof(UtilizationPerc), UtilizationPerc },
				{ nameof(AverageFrequencyMHz), AverageFrequencyMHz },
				{ nameof(PhysicalCoreCount), PhysicalCoreCount },
				{ nameof(ThreadCount), ThreadCount },
				{ nameof(TemperatureC), TemperatureC },
				{ nameof(LastUpdate), LastUpdate.ToUniversalTime() }
			};
		}
	}

	/// <summary>
	/// Contains data about the system's current RAM, such as free/used bytes. Make sure this is updated using <see cref="MemoryData.UpdateData"/>
	/// </summary>
	public static class MemoryData
	{
		/// <summary>
		/// Total system memory (RAM) in bytes.
		/// </summary>
		public static ulong TotalMemory { get; private set; }
		/// <summary>
		/// Used system memory (RAM) in bytes.
		/// </summary>
		public static ulong UsedMemory { get; private set; }
		/// <summary>
		/// Available system memory (RAM) in bytes.
		/// </summary>
		public static ulong AvailableMemory { get; private set; }

		/// <summary>
		/// Gets the percentage of used system memory (RAM).
		/// </summary>
		public static float UsedMemoryPercent => TotalMemory == 0 ? 0 : MathF.Round((float) UsedMemory / TotalMemory * 100, 2);

		/// <summary>
		/// The last time this object was updated.
		/// </summary>
		public static DateTime LastUpdate { get; private set; }
		/// <summary>
		/// Returns whether the current data is too stale and should be updated.
		/// </summary>
		public static bool ShouldUpdate => LastUpdate + ApiConfig.ApiConfiguration.CacheLifetime < DateTime.Now;

		private static Task? UpdatingTask { get; set; }
		private static readonly Lock UpdatingLock = new();

		/// <summary>
		/// Check if the object's data is stale, if so, update it using <see cref="UpdateData"/>.
		/// </summary>
		public static async Task UpdateDataIfNecessary()
		{
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "RAM Fetch", "Checking for RAM data update..."));
			if (!ShouldUpdate)
			{
				Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "RAM Fetch", "RAM data is up to date."));
				return;
			}
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "RAM Fetch", "Updating RAM data..."));

			var localTask = UpdatingTask;
			if (localTask is null)
			{
				lock (UpdatingLock)
				{
					UpdatingTask ??= UpdateData();
					localTask = UpdatingTask;
				}
			}

			await localTask;
			lock (UpdatingLock)
			{
				UpdatingTask = null;
			}
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "RAM Fetch", "RAM data updated."));
		}

		/// <summary>
		/// Updates the RAM data stored in this MemoryData class with the latest metrics.
		/// Make sure to invoke this as to not serve stale data.
		/// </summary>
		/// <exception cref="Exception">Non-zero shell script exit code, or the output was of invalid length.</exception>
		public static async Task UpdateData()
		{
			var shellTimeout = TimeSpan.FromMilliseconds(2500);
			if (LastUpdate == DateTime.MinValue)
			{
				shellTimeout *= 10;
			}

			var rawOutput = await ShellMethods.RunShell($"{SofaPaths.BaseExecutablePath}/scripts/getMem.sh", ["All"], shellTimeout);
			if (!rawOutput.IsSuccess)
			{
				throw new Exception($"Failed to get RAM data from getMem.sh ({rawOutput.ExitCode})");
			}

			string[] outputArray = rawOutput.StandardOutput.Split('|');
			if (outputArray.Length != 3)
			{
				throw new Exception($"Invalid output length from getMem.sh (Expected 3 entries, got {outputArray.Length}).");
			}

			TotalMemory = ulong.Parse(outputArray[0]);
			UsedMemory = ulong.Parse(outputArray[1]);
			AvailableMemory = ulong.Parse(outputArray[2]);
			LastUpdate = DateTime.Now;
		}

		/// <summary>
		/// Fetch this MemoryData in the form of a Dictionary. Useful for outputting.
		/// </summary>
		/// <returns>This MemoryData as a Dictionary.</returns>
		public static Dictionary<string, dynamic> ToDictionary()
		{
			return new Dictionary<string, dynamic>
			{
				{ nameof(TotalMemory), TotalMemory },
				{ nameof(UsedMemory), UsedMemory },
				{ nameof(AvailableMemory), AvailableMemory },
				{ nameof(UsedMemoryPercent), UsedMemoryPercent },
				{ nameof(LastUpdate), LastUpdate.ToUniversalTime() }
			};
		}
	}

	/// <summary>
	/// Best-effort collection of data about a specific connected battery.
	/// </summary>
	/// <remarks>
	///	Most of this is taken from the Linux kernel.<br/>
	/// More documentation regarding a lot of this can be found here: https://www.kernel.org/doc/Documentation/ABI/testing/sysfs-class-power
	/// </remarks>
	/// <seealso cref="BatteryList"/>
	public sealed class BatteryData
	{
		public BatteryData(string supplyName)
		{
			if (string.IsNullOrWhiteSpace(supplyName))
				throw new ArgumentException("Supply name cannot be null or whitespace.", nameof(supplyName));

			if (!Directory.Exists($"/sys/class/power_supply/{supplyName}"))
				throw new ArgumentException($"Supplied supply name '{supplyName}' does not exist.", nameof(supplyName));

			var basePath = $"/sys/class/power_supply/{supplyName}";

			SupplyName = supplyName;
			Manufacturer = ParsingMethods.TryReadFile($"{basePath}/manufacturer");
			Name = ParsingMethods.TryReadFile($"{basePath}/model_name") ?? (Manufacturer is not null ? $"{Manufacturer} Battery" : SupplyName);
			Type = File.ReadAllText($"{basePath}/type").TrimEnd();
			Technology = ParsingMethods.TryReadFile($"{basePath}/technology");

			UpdateData().Wait();
		}

		/// <summary>
		/// The name of the battery supply, as it appears in the <c>/sys/class/power_supply/</c> directory. (e.g., 'BAT0')
		/// </summary>
		public string SupplyName { get; }

		/// <summary>
		/// Reports the name of the device manufacturer.
		/// </summary>
		/// <remarks>
		///	Found in <c>/sys/class/power_supply/{SupplyName}/manufacturer</c>
		/// </remarks>
		public string? Manufacturer { get; }
		/// <summary>
		/// Reports the name of the device model.
		/// </summary>
		/// <remarks>
		///	Found in <c>/sys/class/power_supply/{SupplyName}/model_name</c>
		/// </remarks>
		public string? Name { get; }
		/// <summary>
		/// Describes the main type of the supply.
		/// </summary>
		/// <remarks>
		///	Either "Battery" or "UPS"<br/>
		/// Found in <c>/sys/class/power_supply/{SupplyName}/type</c>
		/// </remarks>
		public string Type { get; }
		/// <summary>
		///	Describes the battery technology supported by the supply.
		/// </summary>
		/// <remarks>
		///	Either "Unknown", "NiMH", "Li-ion", "Li-poly", "LiFe", "NiCd", or "LiMn"<br/>
		/// Found in <c>/sys/class/power_supply/{SupplyName}/technology</c>
		/// </remarks>
		public string? Technology { get; }
		/// <summary>
		/// Reports whether a battery is present or not in the system.
		/// </summary>
		/// <remarks>
		///	Found in <c>/sys/class/power_supply/{SupplyName}/present</c> (if the file is missing, the battery is assumed to be present)
		/// </remarks>
		public bool Present { get; private set; }
		/// <summary>
		/// Reports whether the battery's status is "Discharging" or not.
		/// </summary>
		/// <remarks>
		///	Processed from <c>/sys/class/power_supply/{SupplyName}/status</c>
		/// </remarks>
		public bool? Discharging { get; private set; }

		/// <summary>
		///	Reports an instant, single VBAT voltage reading for the battery. This value is not averaged/smoothed.
		/// </summary>
		/// <remarks>
		///	Unit is Volts rounded to 2 decimal places.<br/>
		/// Found in <c>/sys/class/power_supply/{SupplyName}/voltage_now</c>
		/// </remarks>
		public float? VoltageNowV { get; private set; }

		/// <summary>
		/// Reports how much the battery can hold, compared to its original design capacity.
		/// </summary>
		/// <remarks>
		///	Unit is percentage rounded to 2 decimal places.<br/>
		/// This is processed from '<see cref="FullChargeWh"/> / <see cref="DesignFullChargeWh"/>'
		/// </remarks>
		public float? Health { get; private set; }

		/// <summary>
		/// Fine grain representation of battery capacity.
		/// </summary>
		/// <remarks>
		///	Unit is percentage. Do note that this is a byte and not a float
		/// </remarks>
		public byte? CapacityPerc { get; private set; }
		/// <summary>
		/// Represents a battery percentage level, above which charging will stop. (usually to protect the battery's health)
		/// </summary>
		/// <remarks>
		///	Unit is percentage (byte).<br/>
		/// Found in <c>/sys/class/power_supply/{SupplyName}/charge_control_end_threshold</c>
		/// </remarks>
		public byte? CapacityLimitPerc { get; private set; }

		/// <summary>
		///	Represents the current battery charge in Wh.
		/// </summary>
		/// <remarks>
		///	Calculated from '<c>/sys/class/power_supply/{SupplyName}/charge_now</c> * <see cref="VoltageNowV"/>'
		/// </remarks>
		public float?  CurrentChargeWh { get; private set; }
		/// <summary>
		/// Represents the last full battery charge in Wh.
		/// </summary>
		/// <remarks>
		///	Calculated from '<c>/sys/class/power_supply/{SupplyName}/charge_full</c> * <see cref="VoltageNowV"/>'
		/// </remarks>
		public float? FullChargeWh { get; private set; }
		/// <summary>
		///	Represents the original design battery capacity in Wh.
		/// </summary>
		/// <remarks>
		///	Calculated from '<c>/sys/class/power_supply/{SupplyName}/charge_full_design</c> * <see cref="VoltageNowV"/>'
		/// </remarks>
		public float? DesignFullChargeWh { get; private set; }

		public uint? CycleCount { get; private set; }


		public async Task UpdateData()
		{
			var supplyNameHash = SupplyName.GetHashCode().ToString("X4");
			Logs.LogBook.Write(new (LogStream.Verbose, $"Battery Fetch [{supplyNameHash}]", $"Updating battery data for '{Name ?? SupplyName}'"));
			var basePath = $"/sys/class/power_supply/{SupplyName}";

			Present = !File.Exists($"{basePath}/present") || (await File.ReadAllTextAsync($"{basePath}/present")).StartsWith('1');
			if (!Present)
			{
				Logs.LogBook.Write(new (LogStream.Verbose, $"Battery Fetch [{supplyNameHash}]", $"Skipping battery data for '{Name ?? SupplyName}'"));
				return;
			}

			Discharging = (await File.ReadAllTextAsync($"{basePath}/status")).StartsWith("Discharging");

			VoltageNowV = await ParsingMethods.TryReadFileAsync<float>($"{basePath}/voltage_now") / 1_000_000F;

			CapacityPerc = await ParsingMethods.TryReadFileAsync<byte>($"{basePath}/capacity");
			CapacityLimitPerc = await ParsingMethods.TryReadFileAsync<byte>($"{basePath}/charge_control_end_threshold");

			if (VoltageNowV.HasValue)
			{
				// Wh = Ah * V
				// Source: https://www.inchcalculator.com/ah-to-wh-calculator/#idx_ah_to_wh_conversion_formula
				var currentChargeMicroAh = await ParsingMethods.TryReadFileAsync<ulong>($"{basePath}/charge_now") / 1_000_000F;
				if (currentChargeMicroAh.HasValue) CurrentChargeWh = MathF.Round(currentChargeMicroAh.Value * VoltageNowV.Value, 2);

				var fullChargeMicroAh = await ParsingMethods.TryReadFileAsync<ulong>($"{basePath}/charge_full") / 1_000_000F;
				if (fullChargeMicroAh.HasValue) FullChargeWh = MathF.Round(fullChargeMicroAh.Value * VoltageNowV.Value, 2);

				var designFullChargeMicroAh = await ParsingMethods.TryReadFileAsync<ulong>($"{basePath}/charge_full_design") / 1_000_000F;
				if (designFullChargeMicroAh.HasValue) DesignFullChargeWh = MathF.Round(designFullChargeMicroAh.Value * VoltageNowV.Value, 2);

				VoltageNowV = MathF.Round(VoltageNowV.Value, 2);
			}

			if (FullChargeWh.HasValue && DesignFullChargeWh.HasValue)
				Health = MathF.Round(FullChargeWh.Value / DesignFullChargeWh.Value * 100, 2);

			CycleCount = await ParsingMethods.TryReadFileAsync<uint>($"{basePath}/cycle_count");
		}
	}

	/// <summary>
	/// Fetches, updates, and serializes all connected batteries.
	/// </summary>
	/// <seealso cref="BatteryData"/>
	public static class BatteryList
	{
		static BatteryList()
		{
			InitializeList();
			LastUpdate = DateTime.Now + ApiConfig.ApiConfiguration.ClampedCacheLifetime;
		}

		public static readonly List<BatteryData> List = [];

		public static DateTime LastUpdate { get; private set; }
		public static bool ShouldUpdate => LastUpdate + ApiConfig.ApiConfiguration.CacheLifetime < DateTime.Now;
		public static Task? UpdatingTask { get; private set; }
		private static readonly Lock UpdatingLock = new();

		private static void InitializeList()
		{
			if (List.Count > 0) List.Clear();
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "Battery Fetch", "Loading battery list"));

			foreach (var battery in Directory.EnumerateDirectories("/sys/class/power_supply/"))
			{
				var batteryName = Path.GetFileName(battery);
				if (File.Exists(Path.Combine(battery, "present")) && !File.ReadAllText(Path.Combine(battery, "present")).StartsWith('1') )
				{
					Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "Battery Fetch", $"Battery '{batteryName}' is not present, skipping."));
					continue;
				}
				if (File.ReadAllText(Path.Combine(battery, "type")).TrimEnd() is "Mains" or "USB" or "Wireless")
				{
					Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "Battery Fetch", $"Battery '{batteryName}' does not seem like a battery, skipping."));
					continue;
				}

				try
				{
					List.Add(new BatteryData(batteryName));
				}
				catch (Exception e)
				{
					Logs.LogBook.Write(new (LogStream.Error, "Battery Fetch", $"Error loading battery '{batteryName}': {e.Message}'"));
				}
			}

			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "Battery Fetch", $"Loaded {List.Count} batteries."));
		}

		public static async Task UpdateData()
		{
			var powerSupplyDirs = Directory.EnumerateDirectories("/sys/class/power_supply/").ToList();
			if (powerSupplyDirs.Count != List.Count ||
			    List.All(battery => powerSupplyDirs.Contains(battery.SupplyName)))
			{
				InitializeList();
			}
			else
			{
				foreach (var battery in List)
				{
					await battery.UpdateData();
				}
			}
			LastUpdate = DateTime.Now;
		}
		public static async Task UpdateDataIfNecessary()
		{
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "Battery Fetch", "Checking for battery data update..."));
			if (!ShouldUpdate)
			{
				Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "Battery Fetch", "Battery data is up to date."));
				return;
			}
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "Battery Fetch", "Updating battery data..."));

			var localTask = UpdatingTask;
			if (localTask is null)
			{
				lock (UpdatingLock)
				{
					UpdatingTask ??= UpdateData();
					localTask = UpdatingTask;
				}
			}

			await localTask;
			lock (UpdatingLock)
			{
				UpdatingTask = null;
			}
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "Battery Fetch", "Battery data updated."));
		}

		public static Dictionary<string, dynamic?>[] ToDictionary()
		{
			var batteryDictionaries = new List<Dictionary<string, dynamic?>>(List.Count);

			batteryDictionaries.AddRange(List.Select(batteryData => new Dictionary<string, dynamic?>
			{
				{ nameof(batteryData.Name), batteryData.Name },
				{ nameof(batteryData.Manufacturer), batteryData.Manufacturer },
				{ nameof(batteryData.Type), batteryData.Type },
				{ nameof(batteryData.Technology), batteryData.Technology },
				{ nameof(batteryData.Present), batteryData.Present },
				{ nameof(batteryData.Discharging), batteryData.Discharging },

				{ nameof(batteryData.VoltageNowV), batteryData.VoltageNowV },

				{ nameof(batteryData.Health), batteryData.Health },
				{ nameof(batteryData.CapacityPerc), batteryData.CapacityPerc },
				{ nameof(batteryData.CapacityLimitPerc), batteryData.CapacityLimitPerc },
				{ nameof(batteryData.CurrentChargeWh), batteryData.CurrentChargeWh },
				{ nameof(batteryData.FullChargeWh), batteryData.FullChargeWh },
				{ nameof(batteryData.DesignFullChargeWh), batteryData.DesignFullChargeWh },

				{ nameof(batteryData.CycleCount), batteryData.CycleCount },

				{ nameof(LastUpdate), LastUpdate.ToUniversalTime() }
			}));

			return batteryDictionaries.ToArray();
		}
	}

	public static IPAddress GetLocalIpAddress()
	{
		IPAddress localIp;

		if (Environment.GetEnvironmentVariable("SOFA_LOCALIP") != null)
		{
			if (IPAddress.TryParse(Environment.GetEnvironmentVariable("SOFA_LOCALIP"), out var localIpParsed))
			{
				localIp = localIpParsed;
				Logs.LogBook.Write(new(LogStream.Info, "Network Initalization", $"Using SOFA_LOCALIP environment variable to override detected IP address: '{localIp}'"));
				return localIp;
			}

			Logs.LogBook.Write(new (LogStream.Warning, "Network Initalization",
				$"SOFA_LOCALIP environment variable is set to '{Environment.GetEnvironmentVariable("SOFA_LOCALIP")}', but it doesn't appear to be a valid IP address."));
		}

		using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
		socket.Connect("1.1.1.1", 65530);
		var endPoint = socket.LocalEndPoint as IPEndPoint ?? IPEndPoint.Parse(ShellMethods.RunShell(
			$"{SofaPaths.BaseExecutablePath}/scripts/getNet.sh", ["LocalAddress"]).Result.StandardOutput);
		localIp = endPoint.Address;

		return localIp;
	}
}