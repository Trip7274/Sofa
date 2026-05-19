using System.Text.RegularExpressions;

namespace Sofa_API.StatHandlers;
/// <summary>
/// Contains all the methods and classes to fetch stats about anything disk or filesystem related.
/// </summary>
public static partial class DiskHandling
{
	/// <summary>
	/// Represents disk-related data and properties for a specific mount point.
	/// </summary>
	public sealed class DiskData
	{
		public DiskData(string mountPoint, string mountName, string[] scriptSupports)
		{
			MountPoint = mountPoint;
			MountName = mountName;
			ScriptSupports = scriptSupports;

			UpdateData();
		}

		/// <summary>
		/// Refresh all data inside this object.
		/// </summary>
		internal void UpdateData()
		{
			if (!Directory.Exists(MountPoint))
			{
				IsMissing = true;
				return;
			}


			DevicePath = GetStat("Device.Path", MountPoint, ScriptSupports);
			FileSystem = GetStat("Device.Filesystem", DevicePath, ScriptSupports);
			IsMissing = false;
			TotalSize = ulong.Parse(GetStat("Partition.TotalSpace", MountPoint, ScriptSupports));
			FreeSize = ulong.Parse(GetStat("Parition.FreeSpace", MountPoint, ScriptSupports));

			GetDiskTemperature();
		}

		/// <summary>
		/// "Fill in" the temperature and name data of this DiskData.
		/// </summary>
		private void GetDiskTemperature()
		{
			if (IsMissing || DevicePath is null) return;

			string? hwmonPath = null;
			foreach (string hwmonDir in Directory.EnumerateDirectories("/sys/class/hwmon/"))
			{
				// Search for the hwmon directory for the same device as what we're targeting.
				if (Directory.Exists(
					    Path.Combine(hwmonDir, "device", Path.GetFileNameWithoutExtension(DevicePath).Split('p')[0])))
				{
					hwmonPath = hwmonDir;
				}
			}
			if (hwmonPath is null) return; // Failed to find the appropriate hwmon directory


			if (File.Exists($"{hwmonPath}/device/model"))
			{
				DeviceName = File.ReadAllText($"{hwmonPath}/device/model").TrimEnd('\n').TrimEnd(' ');
			}
			else if (File.Exists($"{hwmonPath}/name"))
			{
				DeviceName = File.ReadAllText($"{hwmonPath}/name").TrimEnd('\n').TrimEnd(' ');
			}

			if (File.Exists($"{hwmonPath}/temp1_input"))
			{
				TemperatureC = float.Parse(File.ReadAllText($"{hwmonPath}/temp1_input")) / 1000;
			}

			if (File.Exists($"{hwmonPath}/temp1_min"))
			{
				TemperatureMinC = float.Parse(File.ReadAllText($"{hwmonPath}/temp1_min")) / 1000;
			}

			if (File.Exists($"{hwmonPath}/temp1_max"))
			{
				TemperatureMaxC = float.Parse(File.ReadAllText($"{hwmonPath}/temp1_max")) / 1000;
			}

			if (File.Exists($"{hwmonPath}/temp1_crit"))
			{
				TemperatureCritC = float.Parse(File.ReadAllText($"{hwmonPath}/temp1_crit")) / 1000;
			}

			if (File.Exists($"{hwmonPath}/temp1_label"))
			{
				TemperatureLabel = File.ReadAllText($"{hwmonPath}/temp1_label").TrimEnd('\n').TrimEnd(' ');
			}
		}
		private string[] ScriptSupports { get; }

		/// <summary>
		/// Represents the retail device name (e.g., "Lexar SSD NM790 2TB").
		/// </summary>
		/// <remarks>
		///	Some disks may have incorrect names, but this only fetches what the manufacterer titled it.
		/// This defaults to null if the name was not found.
		/// </remarks>
		public string? DeviceName { get; private set; }

		/// <summary>
		/// The user-provided mountpoint of this mount.
		/// </summary>
		/// <remarks>
		///	This'll be available even if the device is invalid/unavailable, as it's user-inputted.
		/// </remarks>
		public string MountPoint { get; }
		/// <summary>
		/// The user-provided mount name. Defaults to "Mount" if none was provided.
		/// </summary>
		/// <remarks>
		///	This'll be available even if the device is invalid/unavailable, as it's user-inputted.
		/// </remarks>
		public string MountName { get; }

		/// <summary>
		/// Represents the physical path to the associated device (e.g., "/dev/sda").
		/// </summary>
		/// <remarks>
		/// This can be null if the device is unavailable (unplugged, etc.).
		/// If the mountpoint is in a virtual filesystem ("/dev", "/sys", etc.), it'll be the same as the <see cref="MountPoint"/> property.
		/// </remarks>
		public string? DevicePath { get; private set; }
		/// <summary>
		/// The filesystem of the associated mount. (e.g., "btrfs", "ext4")
		/// </summary>
		/// <remarks>
		///	Can be null if the mount was missing/invalid.
		/// </remarks>
		public string? FileSystem { get; private set; }

		/// <summary>
		/// Whether the mount seems to be missing or invalid.
		/// </summary>
		/// <remarks>
		///	If this is true, then all the properties other than <see cref="MountPoint"/> and <see cref="MountName"/> will be null.
		/// </remarks>
		public bool IsMissing { get; private set; }


		/// <summary>
		/// Total size of the mount. In bytes.
		/// </summary>
		public ulong TotalSize { get; private set; }
		/// <summary>
		/// The amount of available/free space in the mount. In bytes.
		/// </summary>
		public ulong FreeSize { get; private set; }
		/// <summary>
		/// Number of bytes used up in the mount.
		/// </summary>
		public ulong UsedSize => TotalSize - FreeSize;
		/// <summary>
		/// Percentage of used space in the mount.
		/// </summary>
		public byte UsedSizePercent => (byte) ((float) UsedSize / TotalSize * 100);

		/// <summary>
		/// Represents the label set on the specific temperature sensor by the manufacterer.
		/// </summary>
		/// <remarks>
		///	Temperature reporting is not as reliable as other metrics.
		/// If this is null, then expect the rest of the temperature data to also be null.
		/// </remarks>
		public string? TemperatureLabel { get; private set; }
		/// <summary>
		/// Current temperature as reported by the sensor. In Celsius.
		/// </summary>
		public float? TemperatureC { get; private set; }
		/// <summary>
		/// The minimum operating temperature as reported by the sensor. In Celsius.
		/// </summary>
		public float? TemperatureMinC { get; private set; }
		/// <summary>
		/// The maximum operating temperature as reported by the sensor. In Celsius.
		/// </summary>
		public float? TemperatureMaxC { get; private set; }
		/// <summary>
		/// The critical temperature threshold as reported by the sensor. In Celsius.
		/// </summary>
		public float? TemperatureCritC { get; private set; }
	}

	public static class FullDisksData
	{
		static FullDisksData()
		{
			foreach (var mountPoint in ApiConfig.ApiConfiguration.WatchedMounts)
			{
				DiskDataList.Add(new DiskData(mountPoint.Key, mountPoint.Value, ScriptSupports));
			}
		}

		public static void AddMount(string mountPoint, string mountName)
		{
			DiskDataList.Add(new DiskData(mountPoint, mountName, ScriptSupports));
		}

		public static void RemoveMount(string mountPoint)
		{
			DiskDataList.RemoveAll(diskData => diskData.MountPoint == mountPoint);
		}
		public static async Task UpdateData()
		{
			List<Task> diskTasks = [];
			diskTasks.AddRange(DiskDataList.Select(diskData => Task.Run(diskData.UpdateData)));
			await Task.WhenAll(diskTasks);
			LastUpdate = DateTime.Now;
		}

		/// <summary>
		/// The last time this was updated.
		/// </summary>
		public static DateTime LastUpdate { get; private set; } = DateTime.MinValue;
		/// <summary>
		/// Returns whether the current data is too stale and should be updated.
		/// </summary>
		public static bool ShouldUpdate =>
			LastUpdate + ApiConfig.ApiConfiguration.CacheLifetime < DateTime.Now;
		/// <summary>
		/// Check if the object's data is stale, if so, update it using <see cref="UpdateData"/>.
		/// </summary>
		public static async Task UpdateDataIfNecessary()
		{
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "Disk Fetch", "Checking for disk data update..."));
			if (!ShouldUpdate) return;
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "Disk Fetch", "Updating disk data..."));

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
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "Disk Fetch", "Disk data updated."));
		}

		public static Dictionary<string, dynamic?>[] ToDictionary()
		{
			if (DiskDataList.Count == 0) return [];

			List<Dictionary<string, dynamic?>> diskDataDicts = [];

			foreach (var diskData in DiskDataList)
			{
				if (diskData.IsMissing)
				{
					diskDataDicts.Add(new()
					{
						{ nameof(diskData.MountPoint), diskData.MountPoint },
						{ nameof(diskData.MountName), diskData.MountName },
						{ nameof(diskData.IsMissing), diskData.IsMissing },

						{ nameof(LastUpdate), LastUpdate.ToUniversalTime() }
					});
					continue;
				}

				diskDataDicts.Add(new()
				{
					{ nameof(diskData.DeviceName), diskData.DeviceName },
					{ nameof(diskData.MountPoint), diskData.MountPoint },
					{ nameof(diskData.MountName), diskData.MountName },
					{ nameof(diskData.DevicePath), diskData.DevicePath },
					{ nameof(diskData.FileSystem), diskData.FileSystem },
					{ nameof(diskData.IsMissing), diskData.IsMissing },

					{ nameof(diskData.TotalSize), diskData.TotalSize },
					{ nameof(diskData.FreeSize), diskData.FreeSize },
					{ nameof(diskData.UsedSize), diskData.UsedSize },
					{ nameof(diskData.UsedSizePercent), diskData.UsedSizePercent },

					{ nameof(diskData.TemperatureLabel), diskData.TemperatureLabel },
					{ nameof(diskData.TemperatureC), diskData.TemperatureC },
					{ nameof(diskData.TemperatureMinC), diskData.TemperatureMinC },
					{ nameof(diskData.TemperatureMaxC), diskData.TemperatureMaxC },
					{ nameof(diskData.TemperatureCritC), diskData.TemperatureCritC },

					{ nameof(LastUpdate), LastUpdate.ToUniversalTime() }
				});
			}

			return diskDataDicts.ToArray();
		}

		private static readonly string[] ScriptSupports = ShellMethods.GetScriptSupports($"{SofaPaths.BaseExecutablePath}/scripts/getDisk.sh");

		private static Task? UpdatingTask { get; set; }
		private static readonly Lock UpdatingLock = new();
		public static List<DiskData> DiskDataList { get; private set; } = [];
	}

	/// <summary>
	/// Internal helper method to check if the associated disk-fetching script can fetch a specific stat and defaulting to the C# implementation if not.
	/// </summary>
	/// <param name="statName">The specific stat you'd like to fetch. Check remarks for the full list and expected types.</param>
	/// <param name="devicePath">The mountpoint/physical path of the mount you'd like to target.</param>
	/// <param name="scriptSupports">An array containing the supported stats that the disk-fetching script can fetch. Keep it null if you'd like the method to auto-fetch it for you.</param>
	/// <returns>A string containing the requested stat's output. Do note that it can be a non-string (e.g., long) inside a string. Refer to the remarks for types.</returns>
	/// <exception cref="Exception">The requested stat is not supported by this method.</exception>
	/// <remarks>
	///	Supported stats and expected types:
	/// <list type="bullet">
	///		<item>
	///			<term>"Device.Path" [<c>string</c>] - </term>
	///			<description>Returns the physical device path from a specific path on the filesystem. Example: "/home" results in "/dev/nvme0n1p3".</description>
	///		</item>
	///		<item>
	///			<term>"Device.Filesystem" [<c>string</c>] - </term>
	///			<description>Returns the type of filesystem the parititon uses. Example: "/dev/nvme0n1p3" results in "btrfs".</description>
	///		</item>
	///		<item>
	///			<term>"Partition.TotalSpace" [<c>long</c>] - </term>
	///			<description>Returns the total size of the partition. In bytes.</description>
	///		</item>
	///		<item>
	///			<term>"Parition.FreeSpace" [<c>long</c>] - </term>
	///			<description>Returns the free space in the partition. In bytes.</description>
	///		</item>
	/// </list>
	/// <c>devicePath</c> is expected to be a physical path ("/dev/sda") in all the stats, except for "Device.Path", where it should be a specific path inside the filesystem ("/home")
	/// </remarks>
	/// <seealso cref="GetDevicePath"/>
	/// <seealso cref="GetDeviceFileSystem"/>
	private static string GetStat(string statName, string devicePath, string[]? scriptSupports = null)
	{
		scriptSupports ??= ShellMethods.GetScriptSupports($"{SofaPaths.BaseExecutablePath}/scripts/getDisk.sh");

		if (scriptSupports.Contains(statName))
		{
			string scriptPath = $"{SofaPaths.BaseExecutablePath}/scripts/getDisk.sh";
			var shellProcess = ShellMethods.RunShell(scriptPath, [statName, devicePath]).Result;
			if (!shellProcess.IsSuccess)
			{
				throw new Exception($"Error while running '{scriptPath} {statName}'! (code: {shellProcess.ExitCode})");
			}
			return shellProcess.StandardOutput;
		}

		return statName switch
		{
			"Device.Path" => GetDevicePath(devicePath),
			"Device.Filesystem" => GetDeviceFileSystem(devicePath),
			"Partition.TotalSpace" => new DriveInfo(devicePath).TotalSize.ToString(),
			"Parition.FreeSpace" => new DriveInfo(devicePath).AvailableFreeSpace.ToString(),
			_ => throw new Exception($"Unsupported stat '{statName}'!")
		};
	}

	/// <summary>
	/// Method to fetch the physical device path from a specific path in the filesystem. Example: "/home" results in "/dev/nvme0n1p3".
	/// </summary>
	/// <param name="mountPoint">A path in the filesystem</param>
	/// <returns>The physical path of the device associated with the path on the FS.</returns>
	/// <exception cref="Exception">Something went wrong parsing the <c>df</c> output.</exception>
	private static string GetDevicePath(string mountPoint)
	{
		var regexMatch = DevicePathAndFileSystemRegex()
			.Match(ShellMethods.RunShell("df", [mountPoint, "-T"]).Result.StandardOutput);

		if (regexMatch.Groups.Count != 3) throw new Exception($"Error while parsing device path for '{mountPoint}'!");

		// A bit of a hack, but the GetDeviceFileSystem() function wouldn't work if its input was "devtmpfs" or "tmpfs".
		var devicePathMatch = regexMatch.Groups[1].Value;
		return devicePathMatch is "devtmpfs" or "tmpfs" ? mountPoint : devicePathMatch;
	}

	/// <summary>
	/// Method to fetch the type of filesystem the parititon uses. Example: "/dev/nvme0n1p3" results in "btrfs".
	/// </summary>
	/// <param name="mountPoint">A physical path to the partition's device file.</param>
	/// <returns>The partition's filesystem.</returns>
	/// <exception cref="Exception">Something went wrong parsing the <c>df</c> output.</exception>
	private static string GetDeviceFileSystem(string mountPoint)
	{
		var regexMatch = DevicePathAndFileSystemRegex()
			.Match(ShellMethods.RunShell("df", [mountPoint, "-T"]).Result.StandardOutput);

		if (regexMatch.Groups.Count != 3) throw new Exception($"Error while parsing device path for '{mountPoint}'!");

		return regexMatch.Groups[2].Value == "" ? "???" : regexMatch.Groups[2].Value;
	}

    [GeneratedRegex(@"(/dev/\S+|devtmpfs|tmpfs)\s+(\S+)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex DevicePathAndFileSystemRegex();
}
