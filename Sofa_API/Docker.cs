using System.Buffers.Binary;
using System.Net;
using System.Net.ServerSentEvents;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Sofa_API.StatHandlers;

namespace Sofa_API;

public static class DockerLocal
{
	public static bool IsDockerAvailable => File.Exists("/var/run/docker.sock") && ApiConfig.ApiConfiguration.DockerIntegrationEnabled;

	/// <summary>
	/// Returns whether Docker-Compose is available.
	/// </summary>
	public static bool IsDockerComposeAvailable
	{
		get
		{
			if (!IsDockerAvailable) return false;

			string[] pathsInPathEnv = Environment.GetEnvironmentVariable("PATH")!.Split(':');
			string? composeBinPath = null;
			foreach (var pathInEnv in pathsInPathEnv)
			{
				string[] dockerComposeBins;
				try
				{
					dockerComposeBins = Directory.GetFiles(pathInEnv, "docker-compose", SearchOption.TopDirectoryOnly);
				}
				catch (Exception e) when(e is AccessViolationException or DirectoryNotFoundException)
				{
					continue;
				}
				if (dockerComposeBins.Length == 0) continue;

				composeBinPath = dockerComposeBins[0];
				break;
			}

			return composeBinPath is not null && (File.GetUnixFileMode(composeBinPath) & UnixFileMode.UserExecute) != 0;
		}
	}

	/// <summary>
	/// The default icon to use if a container doesn't have a preferred icon.
	/// </summary>
	private const string GenericIconLink = "https://api.iconify.design/mdi/cube-outline.svg";
	public static string PathToImageDataFile { get; } = Path.Combine(SofaPaths.SubPaths.PathToDockerFolder, "imageData.json");

	/// <summary>
	/// Provides methods and properties for interacting with the system's Docker containers.
	/// </summary>
	public static class DockerContainers
	{
		static DockerContainers()
		{
			var dockerRequest = SendRequest("containers/json?all=true").Result;
			if (!dockerRequest.IsSuccess) throw new Exception($"Docker request failed. ({dockerRequest.Status})\n Got body: {dockerRequest.Body}");
			var dockerOutput = JsonSerializer.Deserialize<JsonElement>(dockerRequest.Body);

			foreach (var containerEntries in dockerOutput.EnumerateArray())
			{
				Containers.Add(new DockerContainer(containerEntries));
			}
			LastUpdate = DateTime.Now + ApiConfig.ApiConfiguration.ClampedCacheLifetime;
		}

		/// <summary>
		/// Fetches the current container list from Docker and updates the <see cref="Containers"/> list.
		/// </summary>
		/// <exception cref="Exception">Something went wrong trying to communicate with the Docker daemon.</exception>
		public static async Task UpdateData()
		{
			var dockerRequest = await SendRequest("containers/json?all=true");
			if (!dockerRequest.IsSuccess) throw new Exception($"Docker request failed. ({dockerRequest.Status})\n Got body: {dockerRequest.Body}");

			var dockerOutput = JsonSerializer.Deserialize<JsonElement>(dockerRequest.Body);

			Containers.Clear();
			foreach (var containerEntries in dockerOutput.EnumerateArray())
			{
				Containers.Add(new DockerContainer(containerEntries));
			}
			LastUpdate = DateTime.Now;
		}

		/// <summary>
		/// Contains the task currently updating the data. Null if no update is currently in progress.
		/// </summary>
		private static Task? UpdatingTask { get; set; }
		/// <summary>
		/// Used to prevent multiple threads from updating the data at the same time.
		/// </summary>
		private static readonly Lock UpdatingLock = new();

		/// <summary>
		/// Check if the container data is too old and needs to be updated. If so, update it.
		/// </summary>
		public static async Task UpdateDataIfNecessary()
		{
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "Docker Container Fetch", "Checking for Docker container data update..."));
			if (!ShouldUpdate) return;
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "Docker Container Fetch", "Updating Docker container data..."));

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
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "Docker Container Fetch", "Docker container data updated."));
		}

		/// <summary>
		/// Fetches all the Docker containers into an array of Dictionaries.
		/// </summary>
		/// <param name="getAllContainers">Whether to include non-running containers. Defaults to include all containers.</param>
		public static Dictionary<string, dynamic?>[] ToDictionary(bool getAllContainers = true)
		{
			List<Dictionary<string, dynamic?>> containersList = [];
			containersList.AddRange(getAllContainers
				? Containers.Select(container => container.ToDictionary())
				: Containers.Where(container => container.State == "running")
					.Select(container => container.ToDictionary()));

			return containersList.ToArray();
		}

		/// <summary>
		/// Returns the standard format of a container's metadata file. Each field can be customized but defaults to null.
		/// </summary>
		/// <param name="prettyName">The pretty name for the container.</param>
		/// <param name="note">The field of Notes for the container.</param>
		/// <param name="preferredIconLink">The preferred icon link for the container. Should be a valid link starting with <c>http(s)://</c>.</param>
		/// <param name="webpageLink">The URL pointing at the container's web UI, if applicable.</param>
		/// <returns>A Dictionary that should be serialized to JSON before being saved to the container's metadata file.</returns>
		public static Dictionary<string, string?> GetDefaultMetadata(string? prettyName = null, string? note = null, string? preferredIconLink = null, string? webpageLink = null)
		{
			return new()
			{
				{ "_comment", "This file indicates that this container is managed by Sofa and contains some details about the container. You are free to edit or delete it. It is okay for some to be null." },
				{ "_types", "All of the values are strings that can be null. (string? type)" },

				{ nameof(DockerContainer.PrettyName), prettyName },
				{ nameof(DockerContainer.Note), note },
				{ nameof(DockerContainer.PreferredIconLink), preferredIconLink },
				{ nameof(DockerContainer.WebpageLink), webpageLink }
			};
		}

		/// <summary>
		/// Contains all the system's Docker containers.
		/// </summary>
		/// <seealso cref="UpdateDataIfNecessary"/>
		public static List<DockerContainer> Containers { get; } = [];

		/// <summary>
		/// The last time this was updated. Used internally.
		/// </summary>
		public static DateTime LastUpdate { get; private set; }

		/// <summary>
		/// Returns whether the current data is too stale and should be updated.
		/// </summary>
		public static bool ShouldUpdate => LastUpdate + ApiConfig.ApiConfiguration.CacheLifetime < DateTime.Now;
	}

	/// <summary>
	/// Represents a Docker container on the system. Provides info and some basic controls.
	/// </summary>
	public sealed class DockerContainer
	{
		public DockerContainer(JsonElement dockerOutput)
		{
			Id = dockerOutput.GetProperty(nameof(Id)).GetString() ?? throw new ArgumentException("Docker container ID is null.");

			_labels = dockerOutput.GetProperty("Labels").Deserialize<Dictionary<string, string?>>()!;

			if (_labels.TryGetValue("com.docker.compose.project.config_files", out string? composePath) && File.Exists(composePath))
			{
				ComposePath = composePath;
			}
			if (ComposePath is not null)
			{
				IsCompose = true;
				var composeDirectory = Path.GetDirectoryName(ComposePath);
				IsManaged = composeDirectory is not null && File.Exists(Path.Combine(composeDirectory, ".SofaMetadata.json"));
			}
			ImageName = dockerOutput.GetProperty("Image").GetString() ?? throw new ArgumentException("Docker container image is null.");

			FillContainerMetadata();
			GetContainerNames(dockerOutput);
			ImageDescription = GetDescription(_labels);
			GetHrefFromLabels();
			IconUrls = GetIconUrls(ImageName, _labels, PreferredIconLink);

			ImageID = dockerOutput.GetProperty(nameof(ImageID)).GetString() ?? throw new ArgumentException("Docker container image ID is null.");
			ImageUrl = GetImageUrl(_labels);
			ImageVersion = GetImageVersion(_labels, [ImageName]);

			Command = dockerOutput.GetProperty(nameof(Command)).GetString() ?? throw new ArgumentException("Docker container command is null.");

			Created = dockerOutput.GetProperty(nameof(Created)).GetInt64();

			State = dockerOutput.GetProperty(nameof(State)).GetString() ?? throw new ArgumentException("Docker container state is null.");
			Status = dockerOutput.GetProperty(nameof(Status)).GetString() ?? throw new ArgumentException("Docker container status is null.");

			NetworkMode = dockerOutput.GetProperty("HostConfig").GetProperty(nameof(NetworkMode)).GetString() ?? throw new ArgumentException("Docker container network mode is null.");
			IpAddress = GetIp(dockerOutput.GetProperty("NetworkSettings"), NetworkMode);

			if (dockerOutput.TryGetProperty("Ports", out var portBindingsElement) && portBindingsElement.GetArrayLength() != 0)
			{
				foreach (var portEntry in dockerOutput.GetProperty("Ports").EnumerateArray())
				{
					PortBindings.Add(new PortBinding(portEntry));
				}
			}

			if (dockerOutput.TryGetProperty("Mounts", out var mountBindingsElement) &&
			    mountBindingsElement.GetArrayLength() != 0)
			{
				foreach (var mountEntry in dockerOutput.GetProperty("Mounts").EnumerateArray())
				{
					MountBindings.Add(new MountBinding(mountEntry));
				}
			}
		}
		/// <summary>
		/// Fetch this DockerContainer object as a Dictionary. Used to serialize to JSON.
		/// </summary>
		public Dictionary<string, dynamic?> ToDictionary()
		{
			List<Dictionary<string, dynamic?>> portBindings = [];
			portBindings.AddRange(PortBindings.Select(portBinding => portBinding.ToDictionary()));

			List<Dictionary<string, string?>> mountBindings = [];
			mountBindings.AddRange(MountBindings.Select(mountBinding => mountBinding.ToDictionary()));

			return new()
			{
				{ nameof(Id), Id },
				{ nameof(Names), Names },

				{ nameof(ImageName), ImageName },
				{ nameof(ImageID), ImageID },
				{ nameof(ImageUrl), ImageUrl },
				{ nameof(ImageVersion), ImageVersion },
				{ nameof(ImageDescription), ImageDescription },

				{ nameof(Command), Command },
				{ nameof(Created), Created },

				{ nameof(State), State },
				{ nameof(Status), Status },

				{ nameof(IsCompose), IsCompose },
				{ nameof(IsManaged), IsManaged },

				{ nameof(IconUrls), IconUrls },

				{ nameof(IpAddress), IpAddress?.ToString() },
				{ nameof(NetworkMode), NetworkMode },
				{ nameof(PortBindings), portBindings },
				{ nameof(MountBindings), mountBindings },

				{ nameof(Note), Note },
				{ nameof(WebpageLink), WebpageLink },

				{ nameof(DockerContainers.LastUpdate), DockerContainers.LastUpdate.ToUniversalTime() }
			};
		}

		/// <summary>
		/// Tries to fetch the container's specific IP Address, using the machine's local IP Address as a fallback in case it's a loopback address. (fetched using <see cref="StatsApi.GetLocalIpAddress"/>)
		/// </summary>
		/// <param name="networkSettingsElement">The <c>NetworkSettings</c> property of the Docker daemon's output (regarding this specific container)</param>
		/// <param name="networkMode">The container's network name (or HostConfig -> NetworkMode)</param>
		/// <returns>The container's IP address, usually the machine's local IP address in case the Docker's returned IP address is a loopback address.</returns>
		private static IPAddress? GetIp(JsonElement networkSettingsElement, string networkMode)
		{
			if (networkMode == "none") return null;

			var machineLocalIp = StatsApi.GetLocalIpAddress();
			if (networkMode == "host") return machineLocalIp;

			try
			{
				var containerNetworkId = networkSettingsElement.GetProperty("Networks").GetProperty(networkMode).GetProperty("NetworkID").GetString();
				var networkInfoRequest = SendRequest($"/networks/{containerNetworkId}").Result;
				if (!networkInfoRequest.IsSuccess) return machineLocalIp;

				var networkInfoJson = JsonSerializer.Deserialize<JsonElement>(networkInfoRequest.Body);
				var networkSubnetCidr = networkInfoJson.GetProperty("IPAM").GetProperty("Config").EnumerateArray().First().GetProperty("Subnet").GetString() ?? throw new NullReferenceException();

				var containerIpString = networkSettingsElement.GetProperty("Networks").GetProperty(networkMode).GetProperty("IPAddress")
					.GetString();
				ArgumentException.ThrowIfNullOrWhiteSpace(containerIpString);
				var containerIpAddress = IPAddress.Parse(containerIpString);


				var subnetNetwork = IPNetwork.Parse(networkSubnetCidr);
				return subnetNetwork.Contains(containerIpAddress) ? machineLocalIp : containerIpAddress;
			}
			catch (Exception)
			{
				return machineLocalIp;
			}
		}
		/// <summary>
		/// Extracts and assigns container names from the provided Docker output. Considers the user's preferred name, if any.
		/// </summary>
		/// <param name="dockerOutput">The JSON data representing the Docker container fetched from the Docker daemon.</param>
		private void GetContainerNames(JsonElement dockerOutput)
		{
			Names = GetNames(_labels, dockerOutput, [ImageName]);

			if (IsManaged && PrettyName is not null)
			{
				Names = Names.Prepend(PrettyName).ToList();
			}
		}
		/// <summary>
		/// Extracts the URL pointing to the container's webpage from the container's labels, if any. Sets <see cref="DockerContainer.WebpageLink"/>.
		/// <br/><br/>
		/// The checked keys are "sofa.url", "glance.url", and "homepage.instance.internal.href", in that order of priority.
		/// </summary>
		private void GetHrefFromLabels()
		{
			if (_labels is null) return;

			if (_labels.TryGetValue("sofa.url", out var href)
			    || _labels.TryGetValue("glance.url", out href)
			    || _labels.TryGetValue("homepage.instance.internal.href", out href))
			{
				WebpageLink = href;
			}
		}
		/// <summary>
		/// Fetches the user's specified <see cref="PrettyName"/>, <see cref="Note"/>, <see cref="WebpageLink"/>,
		/// and <see cref="PreferredIconLink"/> for this container. Ran in the constructor.
		/// </summary>
		/// <exception cref="Exception">The metadata file exists, but Sofa was unable to deserialize the JSON. Make sure it's valid.</exception>
		private void FillContainerMetadata()
		{
			if (!IsManaged) return;
			string composeMetadataPath = Path.Combine(Path.GetDirectoryName(ComposePath) ?? "/", ".SofaMetadata.json");

			var currentMetadata = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(composeMetadataPath))
			                      ?? throw new Exception("Failed to deserialize metadata file. Please make sure it is valid JSON.");

			try
			{
				PrettyName = currentMetadata[nameof(PrettyName)];
				Note = currentMetadata[nameof(Note)];
				PreferredIconLink = currentMetadata[nameof(PreferredIconLink)] ?? PreferredIconLink;
				WebpageLink = currentMetadata[nameof(WebpageLink)];
			}
			catch (KeyNotFoundException)
			{
				Logs.LogBook.Write(new LogEntry(LogStream.Error, "Docker Container Init", $"The metadata file for a Docker container seems like invalid JSON. (ID: {Id[..16]})"));
			}
		}

		/// <summary>
		/// Send the command to start this container.
		/// </summary>
		/// <returns>The result of the command.</returns>
		public async Task<IResult> Start()
		{
			if (State is "running") return Results.StatusCode(StatusCodes.Status304NotModified);
			var dockerRequest = await SendRequest($"containers/{Id}/start", HttpMethod.Post);
			return dockerRequest.ToResult();
		}
		/// <summary>
		/// Send the command to restart this container.
		/// </summary>
		/// <returns>The result of the command.</returns>
		public async Task<IResult> Restart()
		{
			if (State is "restarting") return Results.StatusCode(StatusCodes.Status304NotModified);
			var dockerRequest = await SendRequest($"containers/{Id}/restart", HttpMethod.Post);
			return dockerRequest.ToResult();
		}
		/// <summary>
		/// Send the command to stop this container.
		/// </summary>
		/// <returns>The result of the command.</returns>
		public async Task<IResult> Stop()
		{
			if (State is "exited") return Results.StatusCode(StatusCodes.Status304NotModified);
			var dockerRequest = await SendRequest($"containers/{Id}/stop", HttpMethod.Post);
			return dockerRequest.ToResult();
		}
		/// <summary>
		/// Send the command to kill this container.
		/// </summary>
		/// <returns>The result of the command.</returns>
		public async Task<IResult> Kill()
		{
			if (State is "exited") return Results.StatusCode(StatusCodes.Status304NotModified);
			var dockerRequest = await SendRequest($"containers/{Id}/kill", HttpMethod.Post);
			return dockerRequest.ToResult();
		}
		/// <summary>
		/// Send the command to pause this container.
		/// </summary>
		/// <returns>The result of the command.</returns>
		public async Task<IResult> Pause()
		{
			if (State is "paused") return Results.StatusCode(StatusCodes.Status304NotModified);
			if (State is not "running") return Results.Conflict("Cannot pause a container that is not running.");

			var dockerRequest = await SendRequest($"containers/{Id}/pause", HttpMethod.Post);
			return dockerRequest.ToResult();
		}
		/// <summary>
		/// Send the command to unpause/resume this container.
		/// </summary>
		/// <returns>The result of the command.</returns>
		public async Task<IResult> Unpause()
		{
			if (State is not "paused") return Results.StatusCode(StatusCodes.Status304NotModified);
			var dockerRequest = await SendRequest($"containers/{Id}/unpause", HttpMethod.Post);
			return dockerRequest.ToResult();
		}
		/// <summary>
		/// Send the command to delete this container. Optionally deletes the container's volumes and compose directory.
		/// </summary>
		/// <param name="deleteCompose">Please do be careful using this. It will recursively delete the parent directory of the compose file (<c>~/composeFolder/docker-compose.yml</c> would delete <c>~/composeFolder</c>)</param>
		/// <param name="removeVolumes">Delete the anonymous volumes used by the container.</param>
		/// <param name="force">Whether to kill the container before deletion if it's still active.</param>
		/// <returns>The result of the command.</returns>
		public async Task<IResult> Delete(bool deleteCompose = false, bool removeVolumes = false, bool force = false)
		{
			if (State is not "exited" && !force) return Results.Conflict("Cannot delete a container that is not exited.");
			var dockerRequest = await SendRequest($"containers/{Id}?v={removeVolumes}&force={force}", HttpMethod.Delete);
			if (deleteCompose && dockerRequest.IsSuccess && ComposePath is not null)
			{
				Directory.Delete(Path.GetDirectoryName(ComposePath)!, true);
			}
			return dockerRequest.ToResult();
		}

		public static readonly List<string> SupportedLabels = [nameof(PrettyName), nameof(Note), nameof(PreferredIconLink), nameof(WebpageLink)];
		/// <summary>
		/// Provides logic to modify a container's metadata.
		/// </summary>
		/// <param name="props">A dictionary with one of the keys <see cref="PrettyName"/>, <see cref="Note"/>, <see cref="WebpageLink"/>, or <see cref="PreferredIconLink"/> and their new values.</param>
		/// <returns>Whether anything was actually modified or not.</returns>
		/// <exception cref="Exception">Failed to deserialize the metadata file. Probably due to malformed JSON.</exception>
		public async Task<bool> SetContainerMetadata(Dictionary<string, string?> props)
		{
			if (!IsManaged) return false;
			bool changedAnything = false;

			string composeMetadataFile = Path.Combine(Path.GetDirectoryName(ComposePath) ?? "/", ".SofaMetadata.json");
			await using var composeMetadataFileStream = File.Open(composeMetadataFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

			var currentMetadata = await JsonSerializer.DeserializeAsync<Dictionary<string, string?>>(composeMetadataFileStream) ?? throw new Exception("Failed to deserialize metadata file. Please make sure it is valid JSON.");
			foreach (var prop in props.Where(prop => SupportedLabels.Contains(prop.Key)))
			{
				switch (prop.Key)
				{
					case nameof(PrettyName) when PrettyName != prop.Value:
					{
						PrettyName = prop.Value;
						currentMetadata[nameof(PrettyName)] = prop.Value;
						changedAnything = true;
						break;
					}
					case nameof(Note) when Note != prop.Value:
					{
						Note = prop.Value;
						currentMetadata[nameof(Note)] = prop.Value;
						changedAnything = true;
						break;
					}
					case nameof(PreferredIconLink) when PreferredIconLink != prop.Value:
					{
						PreferredIconLink = prop.Value ?? GenericIconLink;
						currentMetadata[nameof(PreferredIconLink)] = prop.Value;
						changedAnything = true;
						break;
					}
					case nameof(WebpageLink) when WebpageLink != prop.Value:
					{
						WebpageLink = prop.Value;
						currentMetadata[nameof(WebpageLink)] = prop.Value;
						changedAnything = true;
						break;
					}
				}
			}

			if (!changedAnything) return false;

			composeMetadataFileStream.SetLength(0);
			await JsonSerializer.SerializeAsync(composeMetadataFileStream, currentMetadata, ApiConfig.SofaJsonSerializerOptions);
			return true;
		}

		public string Id { get; }
		/// <summary>
		/// The possible names of the container. In order of priority/confidence
		/// </summary>
		/// <remarks>
		/// In the case of a managed container, this includes the user's preferred name as the first entry.
		/// </remarks>
		/// <seealso cref="DockerLocal.GetNames"/>
		public List<string> Names { get; private set; } = [];
		private readonly Dictionary<string, string>? _labels;

		public string ImageName { get; }
		// ReSharper disable once InconsistentNaming
		public string ImageID { get; }
		/// <summary>
		/// The URL pointing to the image's public homepage (GitHub link, Docker Hub link, etc.).
		/// </summary>
		/// <seealso cref="DockerLocal.GetImageUrl"/>
		public string? ImageUrl { get; }
		/// <summary>
		/// The version of the image.
		/// </summary>
		/// <remarks>
		///	In cases where the container's labels don't specify a version, Sofa will try to infer it from the image's name. (e.g. "sofa/sofa:latest" -> "latest")
		/// </remarks>
		/// <seealso cref="DockerLocal.GetImageVersion"/>
		public string? ImageVersion { get;  }
		/// <summary>
		/// The friendly description of the image.
		/// </summary>
		/// <seealso cref="DockerLocal.GetDescription"/>
		public string? ImageDescription { get; private set; }

		/// <summary>
		/// The (abs.) path to the Docker compose file that created this container. If null, it means the container is not a Docker compose container.
		/// </summary>
		public string? ComposePath { get; }

		/// <summary>
		/// The command used to start the Docker container (internally).
		/// </summary>
		public string Command { get; }
		/// <summary>
		/// The Unix timestamp of when the container was created.
		/// </summary>
		public long Created { get; }

		/// <summary>
		/// Represents the container's current state.
		/// </summary>
		/// <remarks>
		///	Enum of "created", "running", "paused", "restarting", "exited", "removing", or "dead".
		/// </remarks>
		public string State { get; }
		/// <summary>
		/// Additional human-readable status of this container (e.g., <c>Exit 0</c>)
		/// </summary>
		public string Status { get; }

		/// <summary>
		/// Whether the container is a Docker compose container.
		/// </summary>
		public bool IsCompose { get; }
		/// <summary>
		/// Whether the container is managed by Sofa. If false, the user did not allow Sofa to manage this container, and thus it should not be modified.
		/// </summary>
		public bool IsManaged { get; }

		/// <summary>
		/// List of URLs pointing to the container's icon. Resolved from the container's labels, if any.
		/// </summary>
		/// <remarks>
		///	Ordered by priority/confidence.
		/// </remarks>
		/// <seealso cref="DockerLocal.GetIconUrls"/>
		public List<string> IconUrls { get; }

		/// <summary>
		/// The IP address assigned to the Docker container. Null if the container's <see cref="NetworkMode"/> is none.
		/// </summary>
		public IPAddress? IpAddress { get; }

		/// <summary>
		/// Networking mode (<c>host</c>, <c>none</c>, <c>container:{id}</c>) or name of the primary network the container is using.
		/// </summary>
		public string NetworkMode { get; }
		public List<PortBinding> PortBindings { get; } = [];
		public List<MountBinding> MountBindings { get; } = [];
		/// <summary>
		/// Fetches the container's current metrics (CPU, Memory, and Network usage).
		/// </summary>
		/// <remarks>
		///	This does take a second or so to fetch, mostly on the Docker daemon's side.
		/// </remarks>
		public ContainerStats? Stats => State == "running" ? new(Id) : null;

		/// <summary>
		/// The user's preferred name for this container. Fetched from the container's metadata.
		/// </summary>
		public string? PrettyName { get; private set; }

		/// <summary>
		/// The user-set note for this container. Fetched from the container's metadata.
		/// </summary>
		public string? Note { get; private set; }
		/// <summary>
		/// The user's preferred icon link for this container. Fetched from the container's metadata.
		/// </summary>
		public string PreferredIconLink { get; private set; } = GenericIconLink;
		/// <summary>
		/// The user-set webpage/web UI link for this container. Fetched from the container's metadata.
		/// </summary>
		public string? WebpageLink { get; private set; }
	}

	/// <summary>
	/// Represents a Docker container's metrics.
	/// </summary>
	public sealed class ContainerStats
	{
		public ContainerStats(string containerId)
		{
			var dockerRequest = SendRequest($"/containers/{containerId}/stats?stream=false").Result;
			if (!dockerRequest.IsSuccess) throw new Exception($"Docker request for stats failed. ({dockerRequest.Status})\n Got body: {dockerRequest.Body}");

			var dockerOutput = JsonSerializer.Deserialize<JsonElement>(dockerRequest.Body);

			// These are required to calculate the CPU usage.
			// https://docs.docker.com/reference/api/engine/version/v1.51/#tag/Container/operation/ContainerStats
			//	- cpu_delta = cpu_stats.cpu_usage.total_usage - precpu_stats.cpu_usage.total_usage
			//	- system_cpu_delta = cpu_stats.system_cpu_usage - precpu_stats.system_cpu_usage
			//	- number_cpus = length(cpu_stats.cpu_usage.percpu_usage) or cpu_stats.online_cpus
			//	- CPU usage % = (cpu_delta / system_cpu_delta) * number_cpus * 100.0

			var cpuProperty = dockerOutput.GetProperty("cpu_stats");
			var cpuDelta = cpuProperty.GetProperty("cpu_usage").GetProperty("total_usage").GetUInt64()
			               - dockerOutput.GetProperty("precpu_stats").GetProperty("cpu_usage").GetProperty("total_usage").GetUInt64();
			var systemCpuDelta = cpuProperty.GetProperty("system_cpu_usage").GetUInt64()
			                     - dockerOutput.GetProperty("precpu_stats").GetProperty("system_cpu_usage").GetUInt64();
			var cpuCount = cpuProperty.GetProperty("online_cpus").GetUInt32();

			CpuUtilizationPerc = MathF.Round((float) cpuDelta / systemCpuDelta * cpuCount * 100, 2);

			var memoryProperty = dockerOutput.GetProperty("memory_stats");
			UsedMemory = memoryProperty.GetProperty("usage").GetUInt64();
			TotalMemory = memoryProperty.GetProperty("limit").GetUInt64();

			var networkEntry = dockerOutput.GetProperty("networks").EnumerateObject().First().Value;

			RecievedNetworkBytes = networkEntry.GetProperty("rx_bytes").GetUInt64();
			SentNetworkBytes = networkEntry.GetProperty("tx_bytes").GetUInt64();
		}
		/// <summary>
		/// Fetch this ContainerStats object as a Dictionary. Used to serialize to JSON.
		/// </summary>
		/// <returns>Dictionary representation of this ContainerStats object.</returns>
		public Dictionary<string, dynamic> ToDictionary()
		{
			return new()
			{
				{ nameof(CpuUtilizationPerc), CpuUtilizationPerc },
				{ nameof(UsedMemory), UsedMemory },
				{ nameof(TotalMemory), TotalMemory },
				{ nameof(AvailableMemory), AvailableMemory },
				{ nameof(UsedMemoryPercent), UsedMemoryPercent },
				{ nameof(RecievedNetworkBytes), RecievedNetworkBytes },
				{ nameof(SentNetworkBytes), SentNetworkBytes }
			};
		}

		/// <summary>
		/// Percentage of CPU usage by this container.
		/// </summary>
		public float CpuUtilizationPerc { get; }
		/// <summary>
		/// Bytes of memory used by this container.
		/// </summary>
		public ulong UsedMemory { get; }
		/// <summary>
		/// Total bytes of memory usable to this container.
		/// </summary>
		public ulong TotalMemory { get; }
		/// <summary>
		/// Gets the bytes of memory available to this container.
		/// </summary>
		public ulong AvailableMemory => TotalMemory - UsedMemory;
		/// <summary>
		/// Gets the percentage of memory used by this container.
		/// </summary>
		public float UsedMemoryPercent => TotalMemory == 0 ? 0 : MathF.Round((float) UsedMemory / TotalMemory * 100, 2);

		/// <summary>
		/// Total bytes of network traffic received by this container.
		/// </summary>
		public ulong RecievedNetworkBytes { get; }
		/// <summary>
		/// Total bytes of network traffic sent by this container.
		/// </summary>
		public ulong SentNetworkBytes { get; }
	}

	/// <summary>
	/// Represents a single port binding of a Docker container.
	/// </summary>
	/// <param name="portEntry">One of the "Ports" field's entries from the Docker daemon's output about a single container.</param>
	public sealed class PortBinding(JsonElement portEntry)
	{
		/// <summary>
		/// Fetch this PortBinding object as a Dictionary. Used to serialize to JSON.
		/// </summary>
		public Dictionary<string, dynamic?> ToDictionary()
		{
			return new()
			{
				{ "IpAddress", IpAddress },
				{ "PrivatePort", PrivatePort },
				{ "PublicPort", PublicPort },
				{ "Type", Type }
			};
		}

		/// <summary>
		/// Host IP address that the container's port is mapped to.
		/// </summary>
		public string? IpAddress { get; } = portEntry.TryGetProperty("IP", out var ipAddr) ? ipAddr.GetString() : null;
		/// <summary>
		/// Port exposed on the container.
		/// </summary>
		public ushort PrivatePort { get; } = portEntry.GetProperty(nameof(PrivatePort)).GetUInt16();
		/// <summary>
		/// Port exposed on the host
		/// </summary>
		public ushort? PublicPort { get; } = portEntry.TryGetProperty(nameof(PublicPort), out var publicPort) ? publicPort.GetUInt16() : null;
		/// <summary>
		/// Enum of "tcp", "udp", or "sctp".
		/// </summary>
		public string Type { get; } = portEntry.GetProperty(nameof(Type)).GetString()!;
	}

	/// <summary>
	/// Represents a single mount binding of a Docker container.
	/// </summary>
	/// <param name="mountEntry">One of the "Mounts" field's entries from the Docker daemon's output about a single container.</param>
	public sealed class MountBinding(JsonElement mountEntry)
	{
		/// <summary>
		/// Fetch this MountBinding object as a Dictionary. Used to serialize to JSON.
		/// </summary>
		public Dictionary<string, string?> ToDictionary()
		{
			return new()
			{
				{ "Type", Type },
				{ "Source", Source },
				{ "Destination", Destination },
				{ "Mode", Mode }
			};
		}

		/// <summary>
		/// Enum of "bind", "volume", "image", "tmpfs", "npipe", or "cluster".
		/// </summary>
		/// <seealso href="https://docs.docker.com/reference/api/engine/version/v1.51/#tag/Container/operation/ContainerList">Docker API docs for more details.</seealso>
		public string Type { get; } = mountEntry.GetProperty(nameof(Type)).GetString()!;
		/// <summary>
		/// Location of the mount on the host. If the <see cref="Type"/> is "volume", this is the name of the volume.
		/// </summary>
		public string? Source => Type == "volume" ? mountEntry.GetProperty("Name").GetString() : mountEntry.GetProperty(nameof(Source)).GetString();
		/// <summary>
		/// The path relative to the container root where the <see cref="Source"/> is mounted inside the container.
		/// </summary>
		public string Destination { get; } = mountEntry.GetProperty(nameof(Destination)).GetString()!;

		/// <summary>
		/// Access permissions of the mount. "ro" for read-only, "rw" for read-write, or "z" for private.
		/// </summary>
		public string Mode { get; } = mountEntry.GetProperty(nameof(Mode)).GetString()!;
	}

	/// <summary>
	/// Provides methods and properties for interacting with the system's Docker images.
	/// </summary>
	public static class ImagesInfo
	{
		static ImagesInfo()
		{
			var dockerRequest = SendRequest("images/json").Result;
			if (!dockerRequest.IsSuccess) throw new Exception($"Docker request failed. ({dockerRequest.Status})\n Got body: {dockerRequest.Body}");
			var dockerOutput = JsonSerializer.Deserialize<JsonElement>(dockerRequest.Body);

			foreach (var imageEntry in dockerOutput.EnumerateArray())
			{
				Images.Add(imageEntry.Deserialize<ImageInfo>()!);
			}
			LastUpdate = DateTime.Now + ApiConfig.ApiConfiguration.ClampedCacheLifetime;
		}

		/// <summary>
		/// Fetches the current image list from Docker and updates the <see cref="Images"/> list.
		/// </summary>
		/// <exception cref="Exception">Something went wrong trying to communicate with the Docker daemon.</exception>
		public static async Task UpdateData()
		{
			var dockerRequest = await SendRequest("images/json");
			if (!dockerRequest.IsSuccess) throw new Exception($"Docker request failed. ({dockerRequest.Status})\n Got body: {dockerRequest.Body}");

			var dockerOutput = JsonSerializer.Deserialize<JsonElement>(dockerRequest.Body);

			Images.Clear();
			foreach (var imageEntry in dockerOutput.EnumerateArray())
			{
				Images.Add(imageEntry.Deserialize<ImageInfo>()!);
			}
			LastUpdate = DateTime.Now;
		}


		/// <summary>
		/// Contains the task currently updating the data. Null if no update is currently in progress.
		/// </summary>
		private static Task? UpdatingTask { get; set; }
		/// <summary>
		/// Used to prevent multiple threads from updating the data at the same time.
		/// </summary>
		private static readonly Lock UpdatingLock = new();

		/// <summary>
		/// Check if the image list is too old and needs to be updated. If so, update it.
		/// </summary>
		public static async Task UpdateDataIfNecessary()
		{
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "Docker Image Fetch", "Checking for Docker image data update..."));
			if (!ShouldUpdate) return;
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "Docker Image Fetch", "Updating Docker image data..."));

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
			Logs.LogBook.Write(new LogEntry(LogStream.Verbose, "Docker Image Fetch", "Docker image data updated."));
		}

		/// <summary>
		/// Fetches all the Docker images into an array of Dictionaries.
		/// </summary>
		public static Dictionary<string, dynamic?>[] ToDictionary()
		{
			List<Dictionary<string, dynamic?>> imagesList = [];

			imagesList.AddRange(Images.Select(container => container.ToDictionary()));

			return imagesList.ToArray();
		}

		/// <summary>
		/// Contains all the system's Docker images.
		/// </summary>
		/// <seealso cref="UpdateDataIfNecessary"/>
		public static List<ImageInfo> Images { get; } = [];

		/// <summary>
		/// The last time this was updated. Used internally.
		/// </summary>
		public static DateTime LastUpdate { get; private set; }

		/// <summary>
		/// Returns whether the current data is too stale and should be updated.
		/// </summary>
		public static bool ShouldUpdate => LastUpdate + ApiConfig.ApiConfiguration.CacheLifetime < DateTime.Now;
	}
	/// <summary>
	/// Represents a Docker image on the system. Provides info about the image.
	/// </summary>
	public sealed class ImageInfo
	{
		public async Task<IResult> Delete(bool force = false)
		{
			if (Containers > 0 && !force) return Results.Conflict();
			var dockerRequest = await SendRequest($"images/{Id}?force={force}", HttpMethod.Delete);

			return dockerRequest.ToResult();
		}

		public required string Id { get; init; }
		/// <summary>
		/// ID of the parent image. If none exists, this'll be an empty string.
		/// </summary>
		public required string ParentId { get; init; }
		/// <summary>
		///	List of image names/tags in the local image cache that reference this image.
		/// </summary>
		public string[] RepoTags { get; init; } = [];

		/// <summary>
		/// Unix timestamp of when this image was created.
		/// </summary>
		public required long Created { get; init; }
		/// <summary>
		/// Total size of the image including all layers it is composed of.
		/// </summary>
		public long Size { get; init; }
		public Dictionary<string, string>? Labels { get; init; }
		/// <summary>
		/// Total number of containers that use this image.
		/// </summary>
		public int Containers { get; init; }

		// Sofa-specific

		/// <summary>
		/// The possible names of this image. In order of priority/confidence
		/// </summary>
		/// <seealso cref="DockerLocal.GetNames"/>
		public List<string> Names => GetNames(Labels, repoTags:RepoTags);
		/// <summary>
		/// The friendly description of this image.
		/// </summary>
		/// <seealso cref="DockerLocal.GetDescription"/>
		public string? Description => GetDescription(Labels);
		/// <summary>
		/// The URL pointing to this image's public homepage (GitHub link, Docker Hub link, etc.).
		/// </summary>
		/// <seealso cref="DockerLocal.GetImageUrl"/>
		public string? ImageUrl => GetImageUrl(Labels);
		/// <summary>
		/// List of URLs pointing to this image's icon. Resolved from the image's labels, if any. Ordered by priority/confidence.
		/// </summary>
		/// <seealso cref="DockerLocal.GetIconUrls"/>
		public List<string> IconUrls => GetIconUrls(Names.Last(), Labels);
		/// <summary>
		/// The version of this image.
		/// </summary>
		/// <remarks>
		///	In cases where the image's labels don't specify a version, Sofa will try to infer it from the first <see cref="RepoTags"/> element. (e.g. "sofa/sofa:latest" -> "latest")
		/// </remarks>
		/// <seealso cref="DockerLocal.GetImageVersion"/>
		public string? ImageVersion => GetImageVersion(Labels, RepoTags);

		/// <summary>
		/// Fetch this ImageInfo object as a Dictionary. Used to serialize to JSON.
		/// </summary>
		public Dictionary<string, dynamic?> ToDictionary()
		{
			return new()
			{
				{ nameof(Id), Id },

				{ nameof(Names), Names },
				{ nameof(Description), Description },
				{ nameof(ImageUrl), ImageUrl },
				{ nameof(IconUrls), IconUrls },
				{ nameof(ImageVersion), ImageVersion },

				{ nameof(ParentId), ParentId },
				{ nameof(RepoTags), RepoTags },

				{ nameof(Created), Created },
				{ nameof(Size), Size },
				{ nameof(Containers), Containers },

				{ nameof(ImagesInfo.LastUpdate), ImagesInfo.LastUpdate.ToUniversalTime() }
			};
		}
	}

	/// <summary>
	/// Extracts a list of possible names for a container or image based on the provided labels, Docker output, or repository tags. At least one of these must be provided.
	/// </summary>
	/// <param name="labelsDict">A dictionary containing labels associated with the container or image, typically extracted from the Docker daemon's output.</param>
	/// <param name="dockerOutput">A JSON element containing output details from Docker that may hold additional name information.</param>
	/// <param name="repoTags">An array of repository tags to derive names from, typically useful for images.</param>
	/// <returns>A distinct and ordered list of names corresponding to the container or image, in order of priority/confidence.</returns>
	private static List<string> GetNames(Dictionary<string, string>? labelsDict, JsonElement? dockerOutput = null, string[]? repoTags = null)
	{
		List<string> names = [];
		if (labelsDict is not null)
		{
			foreach (var labelKvp in labelsDict)
			{
				switch (labelKvp.Key)
				{
					case "sofa.name":
					case "glance.name":
					case "homepage.name":
					case "com.docker.compose.project":
					case "org.opencontainers.image.title":
						names.Add(labelKvp.Value);
						break;
				}
			}
		}

		if (dockerOutput is not null && dockerOutput.Value.TryGetProperty(nameof(names), out var namesElement) && namesElement.GetArrayLength() > 0)
		{
			List<JsonElement> dockerNames = namesElement.EnumerateArray().ToList();
			dockerNames.RemoveAll(name => name.GetString() is null);

			// If we're going to have to use the Docker default name, let's clean it up first. (remove leading /, e.g. "/jellyfin" => "jellyfin")
			var firstName = dockerNames.First().GetString();
			if (names.Count == 0 && firstName!.StartsWith('/'))
			{
				names.Add(firstName[1..]);
				dockerNames = dockerNames.Skip(1).ToList();
			}

			// Add the rest of the names, if any.
			if (dockerNames.Count > 0)
			{
				names.AddRange(dockerNames.Select(name => name.GetString()!));
			}
		}

		if (repoTags is not null && repoTags.Length > 0)
		{
			var nameToAdd = repoTags.First();
			nameToAdd = nameToAdd.Split(':').First();

			names.Add(nameToAdd);
			if (!nameToAdd.Contains('/')) names.Add("library/" + nameToAdd);
		}

		// If no names have been found yet, try to get the base image name at the very least.
		if (labelsDict is not null && names.Count == 0 && labelsDict.TryGetValue("org.opencontainers.image.ref.name", out var baseImageName))
		{
			names.Add(baseImageName);
		}

		names = names.Distinct().ToList();

		return names;
	}
	/// <summary>
	/// Tries to retrieve a friendly description of the Docker image from its labels.
	/// </summary>
	/// <param name="labelsDict">A dictionary containing labels associated with the container or image, typically extracted from the Docker daemon's output.</param>
	/// <returns>A string representing the image description if a valid description label is found; otherwise, null.</returns>
	private static string? GetDescription(Dictionary<string, string>? labelsDict)
	{
		if (labelsDict is null) return null;
		if (labelsDict.TryGetValue("sofa.description", out var description)
		    || labelsDict.TryGetValue("glance.description", out description)
		    || labelsDict.TryGetValue("homepage.description", out description)
		    || labelsDict.TryGetValue("org.opencontainers.image.description", out description))
		{
			return description;
		}

		return null;
	}

	/// <summary>
	/// Retrieves a list of icon URLs based on the provided label dictionary and an optional preferred icon link. Falls back to <see cref="GenericIconLink"/> if no icon links are found.
	/// </summary>
	/// <param name="imageName">The image's name with the repository. E.g., "jellyfin/jellyfin" or "python"</param>
	/// <param name="labelsDict">A dictionary containing labels associated with the container or image, typically extracted from the Docker daemon's output.</param>
	/// <param name="preferredIconLink">An optional preferred icon link to use first.</param>
	/// <returns>A list of absolute icon URLs.</returns>
	private static List<string> GetIconUrls(string imageName, Dictionary<string, string>? labelsDict, string? preferredIconLink = null)
	{
		List<string> iconUrls = [];
		if (preferredIconLink is not null and not GenericIconLink) iconUrls.Add(GetUrlFromRepos(preferredIconLink));
		if (labelsDict is not null)
		{
			foreach (var labelKvp in labelsDict)
			{
				switch (labelKvp.Key)
				{
					case "sofa.icon":
					case "glance.icon":
					case "com.docker.desktop.extension.icon":
						iconUrls.Add(GetUrlFromRepos(labelKvp.Value));
						break;
				}
			}
		}

		if (CheckImageHasIcons(imageName))
		{
			iconUrls.AddRange(GetImageIconUrls(imageName));
		}

		if (iconUrls.Count == 0)
		{
			iconUrls.Add(GenericIconLink);
		}

		return iconUrls;

		string GetUrlFromRepos(string repoName)
		{
			if (repoName.StartsWith("http")) return repoName;
			if (repoName.StartsWith("di:"))
			{
				repoName = repoName[3..];
				return $"https://cdn.jsdelivr.net/gh/homarr-labs/dashboard-icons/svg/{repoName}.svg";
			}
			if (repoName.StartsWith("sh:"))
			{
				repoName = repoName[3..];
				return $"https://cdn.jsdelivr.net/gh/selfhst/icons/png/{repoName}.png";
			}
			if (repoName.StartsWith("si:"))
			{
				repoName = repoName[3..];
				return $"https://cdn.jsdelivr.net/npm/simple-icons@v15/icons/{repoName}.svg";
			}
			if (repoName.StartsWith("mdi:"))
			{
				repoName = repoName[4..];
				return $"https://api.iconify.design/mdi/{repoName}.svg";
			}

			return repoName;
		}
	}

	/// <summary>
	/// Retrieves the URL pointing to the image's public homepage (e.g., GitHub link, Docker Hub link) from the given Labels dictionary.
	/// </summary>
	/// <param name="labelsDict">A dictionary containing labels associated with the container or image, typically extracted from the Docker daemon's output.</param>
	/// <returns>A string representing the image's public URL if found and starting with "http"; otherwise, null.</returns>
	private static string? GetImageUrl(Dictionary<string, string>? labelsDict)
	{
		if (labelsDict is null) return null;

		if (labelsDict.TryGetValue("sofa.image.url", out var imageUrl) && imageUrl.StartsWith("http")
			|| labelsDict.TryGetValue("org.opencontainers.image.url", out imageUrl) && imageUrl.StartsWith("http")
			|| labelsDict.TryGetValue("org.opencontainers.image.source", out imageUrl) && imageUrl.StartsWith("http")
		    || labelsDict.TryGetValue("com.docker.extension.publisher-url", out imageUrl) && imageUrl.StartsWith("http"))
			return imageUrl;

		return null;
	}

	/// <summary>
	/// Tries to retrieve the version of the image from the given Labels dictionary.
	/// </summary>
	/// <param name="labelsDict">
	/// A dictionary containing labels associated with the container or image, typically extracted from the Docker daemon's output.
	/// </param>
	/// <param name="repoTags">
	/// An optional array of repository tags for the image (or the image's name). Used as a fallback to infer the version if labels do not contain version information.
	/// </param>
	/// <returns>
	/// The version of the image as a string, or null if no version information can be determined.
	/// </returns>
	private static string? GetImageVersion(Dictionary<string, string>? labelsDict, string[]? repoTags = null)
	{
		if (labelsDict is not null)
		{
			if (labelsDict.TryGetValue("sofa.version", out var version)
			    || labelsDict.TryGetValue("org.opencontainers.image.version", out version))
			{
				return version;
			}
		}

		if (repoTags is null || repoTags.Length <= 0 || !repoTags[0].Contains(':')) return null;

		var repoTagVersion = repoTags[0].Split(':').Last();
		return repoTagVersion;
	}

	/// <summary>
	/// Represents the response from a Docker API request, containing status, success state, content-type, and response body.
	/// </summary>
	public sealed record DockerResponse
	{
		/// <summary>
		/// The response's status code.
		/// </summary>
		public HttpStatusCode Status { get; init; }
		/// <summary>
		/// Whether the response had a status code of 200-299. (This considers 300-399 as a failure.)
		/// </summary>
		public bool IsSuccess { get; init; }
		/// <summary>
		/// The response's body text.
		/// </summary>
		public string Body { get; init; } = "";

		/// <summary>
		/// The response's stated content type. Falls back to "application/json" if not specified. (as Docker usually uses JSON)
		/// </summary>
		public string ContentType { get; init; } = "application/json";

		/// <summary>
		/// Return this object to a <see cref="IResult"/> object. Useful to return as a response verbatim.
		/// </summary>
		/// <remarks>
		///	Do be careful with this, as it may expose sensitive information. If possible, parse the response body into a more specific object.
		/// </remarks>
		public IResult ToResult()
		{
			return Results.Text(Body, ContentType, Encoding.UTF8, (int) Status);
		}
	}

	/// <summary>
	/// Creates and returns an instance of <see cref="HttpClient"/> configured to communicate with the Docker socket.
	/// </summary>
	/// <returns>An <see cref="HttpClient"/> instance configured to use a Unix domain socket for communication.</returns>
	private static HttpClient GetDockerClient()
	{
		var handler = new SocketsHttpHandler
		{
			// ReSharper disable once UnusedParameter.Local
			ConnectCallback = async (context, cancellationToken) =>
			{
				var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
				await socket.ConnectAsync(new UnixDomainSocketEndPoint("/var/run/docker.sock"), cancellationToken);
				return new NetworkStream(socket, true);
			}
		};

		return new HttpClient(handler)
		{
			BaseAddress = new Uri("http://localhost")
		};
	}

	/// <summary>
	/// Streams Docker logs for the specified container, writing the logs to the HTTP response in real-time.
	/// </summary>
	/// <param name="containerId">The ID of the Docker container for which logs should be streamed. Must include at least the first 12 characters.</param>
	/// <param name="stdout">Indicates whether standard output logs should be included in the stream.</param>
	/// <param name="stderr">Indicates whether standard error logs should be included in the stream.</param>
	/// <param name="timestamps">Indicates whether log timestamps should be included in the stream.</param>
	/// <param name="cancellationToken">Cancellation token for the enumerator</param>
	/// <returns>An <see cref="SseItem{T}"/> containing a byte[] with the event type "newLogEntry". The array includes both the entry's header and body.</returns>
	/// <exception cref="InvalidOperationException">The Stream to the Docker socket is not readable.</exception>
	public static async IAsyncEnumerable<SseItem<byte[]>> StreamDockerLogs(string containerId, bool stdout, bool stderr, bool timestamps, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		using var dockerClient = GetDockerClient();

		await using var dockerStream = await dockerClient.GetStreamAsync(
			$"http://localhost/v1.51/containers/{containerId}/logs?stdout={stdout}&stderr={stderr}&timestamps={timestamps}&follow=true", cancellationToken);

		if (!dockerStream.CanRead)
		{
			throw new InvalidOperationException("Unable to read Docker logs stream.");
		}
		byte[] logHeader = new byte[8];


		while (!cancellationToken.IsCancellationRequested)
		{
			var bytesRead = await dockerStream.ReadAtLeastAsync(logHeader, 8, false, cancellationToken);

			if (bytesRead < 8 || cancellationToken.IsCancellationRequested)
			{
				break;
			}
			var payloadSize = (int) BinaryPrimitives.ReadUInt32BigEndian(logHeader.AsSpan(4));

			var payloadBuffer = new byte[payloadSize];
			await dockerStream.ReadExactlyAsync(payloadBuffer, 0, payloadSize, cancellationToken);

			yield return new SseItem<byte[]>(logHeader.Concat(payloadBuffer).ToArray(), "newLogEntry");
		}
	}

	/// <summary>
	/// Pulls the specified image from Docker Hub. Streams the progress to the HTTP response in real-time using SSE.
	/// </summary>
	/// <param name="context">The HTTP context to write the stream to.</param>
	/// <param name="imageName">The image's name, or its specific digest to fetch.</param>
	/// <param name="tagOrDigest">The tag to use. Will default to 'latest' if no digest or tag was specified.</param>
	public static async Task PullImage(HttpContext context, string? imageName, string tagOrDigest = "latest")
	{
		if (!IsDockerAvailable)
		{
			context.Response.StatusCode = 500;
			context.Response.ContentType = "text/plain";
			await context.Response.WriteAsync("Docker is not available on this system.", context.RequestAborted);
			await context.Response.CompleteAsync();
			return;
		}

		if (string.IsNullOrWhiteSpace(imageName))
		{
			context.Response.StatusCode = 400;
			context.Response.ContentType = "text/plain";
			await context.Response.WriteAsync("No image name or digest provided.", context.RequestAborted);
			await context.Response.CompleteAsync();
			return;
		}
		if (imageName.Contains(':'))
		{
			tagOrDigest = imageName.Split(':').Last();
			imageName = imageName.Split(':')[0];
		}

		var responseToClient = context.Response;

		Task<string[]>? iconFetchTask = null;
		bool imageHasIcons = CheckImageHasIcons(imageName);
		if (!imageHasIcons)
		{
			iconFetchTask = Task.Run(() => LoadImageIcons(imageName, new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token));
		}

		using var client = GetDockerClient();

		try
		{
			var request = new HttpRequestMessage(HttpMethod.Post, $"/images/create?fromImage={imageName}&tag={tagOrDigest}");
			var dockerResponse = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

			switch (dockerResponse.StatusCode)
			{
				case HttpStatusCode.NotFound:
				{
					responseToClient.StatusCode = 404;
					await responseToClient.WriteAsync("Repository does not exist, or no read access.", context.RequestAborted);
					await responseToClient.CompleteAsync();
					return;
				}
				case HttpStatusCode.InternalServerError:
				{
					responseToClient.StatusCode = 500;
					await dockerResponse.Content.CopyToAsync(responseToClient.Body);
					await responseToClient.CompleteAsync();
					return;
				}
			}

			await using var stream = await dockerResponse.Content.ReadAsStreamAsync(context.RequestAborted);
			if (!stream.CanRead)
			{
				responseToClient.StatusCode = 500;
				responseToClient.ContentType = "text/plain";
				await responseToClient.Body.WriteAsync("Unable to read from the Docker socket."u8.ToArray(),
					context.RequestAborted);
				await responseToClient.CompleteAsync();
				return;
			}

			responseToClient.ContentType = "text/event-stream";
			responseToClient.Headers.Append("Cache-Control", "no-cache");
			responseToClient.Headers.Append("Connection", "keep-alive");


			List<byte> responseBuffer = [];
			bool seenCarriageReturn = false;
			async Task SendBuffer()
			{
				await responseToClient.WriteAsync($"data: {Encoding.UTF8.GetString(responseBuffer.ToArray())}\n\n", context.RequestAborted);
				await responseToClient.Body.FlushAsync(context.RequestAborted);
				responseBuffer.Clear();
			}

			while (!context.RequestAborted.IsCancellationRequested)
			{
				var readByte = stream.ReadByte();
				if (readByte == -1) break;
				switch (readByte)
				{
					case '\r':
					{
						seenCarriageReturn = true;
						break;
					}
					case '\n' when seenCarriageReturn:
					{
						await SendBuffer();
						seenCarriageReturn = false;
						break;
					}
					default:
					{
						responseBuffer.Add((byte) readByte);
						break;
					}
				}
			}
			if (responseBuffer.Count > 0) await SendBuffer();
		}
		catch (Exception e)
		{
			await responseToClient.WriteAsync("data: There was an error reading the progress of the pull. The image may still be downloading, and more details will follow.\n\n", context.RequestAborted);
			await responseToClient.WriteAsync($"data: {e.Message}\n\n", context.RequestAborted);
			await responseToClient.Body.FlushAsync(context.RequestAborted);
		}
		finally
		{
			await responseToClient.CompleteAsync();
		}

		if (!imageHasIcons)
		{
			await SetImageIcons(imageName, await iconFetchTask!);
		}
	}

	internal static async Task SetImageIcons(string imageName, string[] imageIconUrls)
	{
		imageName = imageName.ToLowerInvariant();
		if (imageName.Contains(':')) imageName = imageName.Split(':')[0];
		imageName = ParsingMethods.ConvertTextToSlug(imageName);

		var fileStream = new FileStream(PathToImageDataFile, FileMode.OpenOrCreate, FileAccess.ReadWrite);

		Dictionary<string, string[]> fileJson;
		try
		{
			fileJson = await JsonSerializer.DeserializeAsync<Dictionary<string, string[]>>(fileStream) ?? [];
		}
		catch (Exception e)
		{
			Logs.LogBook.Write(new (LogStream.Error, "Docker Pull [iconSet]", $"imageData.json seems invalid and will be regenerated. Your last imageData.json will be moved to imageData.json.old (Error: {e.Message})"));
			File.Move(PathToImageDataFile, PathToImageDataFile + ".old", true);

			await fileStream.DisposeAsync();
			fileStream = new FileStream(PathToImageDataFile, FileMode.Create, FileAccess.ReadWrite);

			fileJson = [];
		}

		fileJson[imageName] = imageIconUrls;
		fileStream.Seek(0, SeekOrigin.Begin);
		await JsonSerializer.SerializeAsync(fileStream, fileJson, ApiConfig.SofaJsonSerializerOptions);

		await fileStream.DisposeAsync();
	}

	public static string[] GetImageIconUrls(string imageName)
	{
		imageName = imageName.ToLowerInvariant();
		if (imageName.Contains(':')) imageName = imageName.Split(':')[0];
		imageName = ParsingMethods.ConvertTextToSlug(imageName);

		Dictionary<string, string[]> fileJson;
		try
		{
			fileJson = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(PathToImageDataFile)) ?? [];
		}
		catch (JsonException e)
		{
			Logs.LogBook.Write(new (LogStream.Error, "Docker Pull [iconFetch]", $"imageData.json seems invalid. Error: {e.Message}"));
			return [];
		}

		return fileJson.TryGetValue(imageName, out var imageIconUrls) ? imageIconUrls : [];
	}

	public static bool CheckImageHasIcons(string imageName)
	{
		imageName = imageName.ToLowerInvariant();
		if (imageName.Contains(':')) imageName = imageName.Split(':')[0];
		imageName = ParsingMethods.ConvertTextToSlug(imageName);

		Dictionary<string, string[]> fileJson;
		try
		{
			fileJson = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(PathToImageDataFile)) ?? [];
		}
		catch (JsonException e)
		{
			Logs.LogBook.Write(new (LogStream.Error, "Docker Pull [iconCheck]", $"imageData.json seems invalid. Error: {e.Message}"));
			return false;
		}

		return fileJson.TryGetValue(imageName, out var imageIconUrls) && imageIconUrls.Length > 0;
	}

	private static readonly HttpClient ImageIconsClient = new();
	public static async Task<string[]> LoadImageIcons(string imageName, CancellationToken cancellationToken = default)
	{
		string?[] iconList = [null, null, null];
		imageName = imageName.Split('/').Last();
		if (imageName.Contains(':')) imageName = imageName.Split(':')[0];
		imageName = imageName.ToLowerInvariant();

		Logs.LogBook.Write(new (LogStream.Verbose, "Image icon fetch", $"Fetching icons for image {imageName}..."));

		Task<HttpResponseMessage>[] fetchTasks =
		[
			ImageIconsClient.GetAsync($"https://cdn.jsdelivr.net/gh/homarr-labs/dashboard-icons/svg/{imageName}.svg", HttpCompletionOption.ResponseHeadersRead, cancellationToken),
			ImageIconsClient.GetAsync($"https://cdn.jsdelivr.net/gh/selfhst/icons/svg/{imageName}.svg", HttpCompletionOption.ResponseHeadersRead, cancellationToken),
			ImageIconsClient.GetAsync($"https://cdn.jsdelivr.net/gh/selfhst/icons/png/{imageName}.png", HttpCompletionOption.ResponseHeadersRead, cancellationToken),
			ImageIconsClient.GetAsync($"https://cdn.jsdelivr.net/npm/simple-icons@v15/icons/{imageName}.svg", HttpCompletionOption.ResponseHeadersRead, cancellationToken)
		];

		await foreach (var fetchTask in Task.WhenEach(fetchTasks).WithCancellation(cancellationToken))
		{
			if (!fetchTask.Result.IsSuccessStatusCode) continue;

			var possibleUrl = fetchTask.Result.RequestMessage?.RequestUri;

			switch (possibleUrl?.LocalPath.Split('/')[2])
			{
				case "homarr-labs":
				{
					iconList[0] = possibleUrl.OriginalString;
					break;
				}
				case "selfhst":
				{
					// If this is an SVG, overwrite the PNG version, but if we haven't found any SVGs, use the PNG
					if (iconList[1] is null || iconList[1]!.EndsWith(".png"))
					{
						iconList[1] = possibleUrl.OriginalString;
					}
					break;
				}
				case "simple-icons@v15":
				{
					iconList[2] = possibleUrl.OriginalString;
					break;
				}
			}
		}

		Logs.LogBook.Write(new (LogStream.Verbose, "Image icon fetch", $"Fetched {iconList.Length} icons for image {imageName}."));
		return iconList.Where(url => url is not null).ToArray()!;
	}

	/// <summary>
	/// Sends a request to the Docker Daemon API using the specified path, HTTP method, and optional content, and retrieves the response.
	/// </summary>
	/// <param name="path">The path to query the Docker API on.</param>
	/// <param name="method">The HTTP method to use for the request. Supported methods are "GET", "POST", and "DELETE". ["GET"].</param>
	/// <param name="content">The optional content to include in the request body, only used for "POST" requests. [""]</param>
	/// <returns>A <see cref="DockerResponse"/> with the response details, contained in a Task.</returns>
	/// <exception cref="FileNotFoundException">Thrown if the Docker socket is not available or the Docker daemon is not running.</exception>
	/// <exception cref="ArgumentException">Thrown if the HTTP method provided is not "GET", "POST", or "DELETE".</exception>
	/// <exception cref="Exception">Thrown if the Docker socket is not readable or writable.</exception>
	public static async Task<DockerResponse> SendRequest(string path, HttpMethod? method = null, string? content = null)
	{
		if (path.StartsWith('/')) path = path[1..];
		if (!IsDockerAvailable)
			throw new FileNotFoundException("Docker socket not found, or the Docker integration was disabled. " +
			                                "Ensure that the Docker daemon is running and that the socket is accessible, " +
			                                "along with that the Docker integration is enabled.");
		method ??= HttpMethod.Get;

		var client = GetDockerClient();
		var request = new HttpRequestMessage(method, $"http://localhost/{path}");
		if (!string.IsNullOrWhiteSpace(content)) request.Content = new StringContent(content);
		var clientResponse = await client.SendAsync(request);

		byte[] fullResponse;
		await using (var stream = await clientResponse.Content.ReadAsStreamAsync())
		{
			if (!stream.CanRead) throw new Exception("Docker UNIX socket is not readable.");

			using var memoryStream = new MemoryStream();
			await stream.CopyToAsync(memoryStream);

			fullResponse = memoryStream.ToArray();
		}

		return new()
		{
			Status = clientResponse.StatusCode,
			IsSuccess = clientResponse.IsSuccessStatusCode,
			Body = Encoding.UTF8.GetString(fullResponse),
			ContentType = clientResponse.Content.Headers.ContentType?.MediaType ?? "application/json"
		};
	}
}

public static class DockerHub
{
	public sealed record TagDetails
	{
		public TagDetails(string name, JsonElement imagesElement)
		{
			Name = name;


			foreach (var imageElem in imagesElement.EnumerateArray())
			{
				if (imageElem.TryGetProperty("digest", out var digestElement) && digestElement.GetString() is null) continue;

				Images.Add(new ImageDetails
				{
					ArchitectureRaw = imageElem.GetProperty("architecture").GetString()!,
					ArchVariant = imageElem.GetProperty("variant").GetString(),
					OsName = imageElem.GetProperty("os").GetString()!,
					Digest = imageElem.GetProperty("digest").GetString()!,
					Size = imageElem.GetProperty("size").GetInt32(),
					LastPushed = imageElem.GetProperty("last_pushed").GetDateTime()
				});
			}
		}

		public Dictionary<string, Dictionary<string, dynamic?>?> ToDictionary(bool filterIncompatible = true)
		{
			var resultDict = new Dictionary<string, Dictionary<string, dynamic?>?>();

			foreach (var image in Images.Where(image => !filterIncompatible || image.IsCompatible()))
			{
				resultDict.Add(image.Digest, image);
			}

			return resultDict;
		}

		public bool ContainsCompatibleImage()
		{
			return Images.Any(image => image.IsCompatible());
		}
		public string Name { get; }
		public List<ImageDetails> Images { get; } = [];
	}

	public sealed record ImageDetails
	{
		public bool IsCompatible()
		{
			var systemArch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
			switch (systemArch)
			{
				case System.Runtime.InteropServices.Architecture.X64 when !string.Equals(Architecture, "amd64", StringComparison.OrdinalIgnoreCase):
				case System.Runtime.InteropServices.Architecture.Arm64 when !string.Equals(Architecture, "arm64", StringComparison.OrdinalIgnoreCase):
				case System.Runtime.InteropServices.Architecture.RiscV64 when !string.Equals(Architecture, "riscv64", StringComparison.OrdinalIgnoreCase):
				case System.Runtime.InteropServices.Architecture.X86 when !string.Equals(Architecture, "386", StringComparison.OrdinalIgnoreCase):
				case System.Runtime.InteropServices.Architecture.Armv6 when !string.Equals(Architecture, "armv6", StringComparison.OrdinalIgnoreCase):
					return false;
			}

			switch (Environment.OSVersion.Platform)
			{
				case PlatformID.Unix when !string.Equals(OsName, "linux", StringComparison.OrdinalIgnoreCase):
				case PlatformID.Win32NT when !string.Equals(OsName, "windows", StringComparison.OrdinalIgnoreCase):
					return false;
			}

			return true;
		}

		public Dictionary<string, dynamic?> ToDictionary()
		{
			return new()
			{
				{ nameof(Architecture), Architecture },
				{ nameof(OsName), OsName },
				{ nameof(Size), Size },
				{ nameof(LastPushed), LastPushed },
				{ nameof(IsCompatible), IsCompatible() }
			};
		}
		public static implicit operator Dictionary<string, dynamic?>(ImageDetails image) => image.ToDictionary();

		public string Architecture => ArchVariant is not null ? ArchitectureRaw + ArchVariant : ArchitectureRaw;
		public required string ArchitectureRaw { get; init; }
		public string? ArchVariant { get; init; }
		public required string OsName { get; init; }
		public required string Digest { get; init; }
		public int Size { get; init; }
		public DateTime? LastPushed { get; init; }
	}
}