using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sofa_API.StatHandlers;

namespace Sofa_API.Endpoints;

public static class StatsEndpoints
{
	private static async IAsyncEnumerable<SseItem<Dictionary<string, dynamic>>> StreamStats(List<ApiConfig.SystemStats> requestedStats, TimeSpan delay, ushort streamId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		Logs.LogBook.Write(new (LogStream.Verbose, $"GetStatsSse [{streamId:X4}]", $"Got a request for streaming stats: {string.Join(", ", requestedStats.Select(stat => stat.ToString()))}"));

		Dictionary<string, dynamic> responseDictionary = [];

		while (!cancellationToken.IsCancellationRequested)
		{
			List<Task> fetchTasks = [];
			try
			{
				foreach (var stat in requestedStats)
				{
					switch (stat)
					{
						case ApiConfig.SystemStats.Cpu:
						{
							fetchTasks.Add(Task.Run(StatsApi.CpuData.UpdateDataIfNecessary, cancellationToken));
							break;
						}

						case ApiConfig.SystemStats.Gpu:
						{
							fetchTasks.Add(Task.Run(GpuHandling.FullGpusData.UpdateDataIfNecessary, cancellationToken));
							break;
						}

						case ApiConfig.SystemStats.Memory:
						{
							fetchTasks.Add(Task.Run(StatsApi.MemoryData.UpdateDataIfNecessary, cancellationToken));
							break;
						}

						case ApiConfig.SystemStats.Mounts:
						{
							fetchTasks.Add(Task.Run(DiskHandling.FullDisksData.UpdateDataIfNecessary, cancellationToken));
							break;
						}
						case ApiConfig.SystemStats.Batteries:
						{
							fetchTasks.Add(Task.Run(StatsApi.BatteryList.UpdateDataIfNecessary, cancellationToken));
							break;
						}
					}
				}

				await Task.WhenAll(fetchTasks);
			}
			catch (OperationCanceledException e)
			{
				Logs.LogBook.Write(new (LogStream.Verbose, $"GetStatsSse [{streamId:X4}]", $"Streaming was cancelled abruptly: {e.Message}"));
				break;
			}

			// Request assembly
			foreach (var requestedStat in requestedStats)
			{
				switch (requestedStat)
				{
					case ApiConfig.SystemStats.Meta:
					{
						responseDictionary.Add("Meta",
							new Dictionary<string, dynamic>
							{
								{ nameof(ApiConfig.Version), ApiConfig.Version },
								{ nameof(ApiConfig.ApiVersion), ApiConfig.ApiVersion },
								{ nameof(ApiConfig.ApiConfiguration.CacheLifetime), ApiConfig.ApiConfiguration.CacheLifetime },
								{ "SofaUptime", ApiConfig.SofaStartStopwatch.Elapsed }
							});
						break;
					}

					case ApiConfig.SystemStats.System:
					{
						responseDictionary.Add("System", StatsApi.GeneralSpecs.ToDictionary());
						break;
					}

					case ApiConfig.SystemStats.Cpu:
					{
						responseDictionary.Add("CPU", StatsApi.CpuData.ToDictionary());
						break;
					}

					case ApiConfig.SystemStats.Gpu:
					{
						responseDictionary.Add("GPU", GpuHandling.FullGpusData.ToDictionary());
						break;
					}

					case ApiConfig.SystemStats.Memory:
					{
						responseDictionary.Add("Memory", StatsApi.MemoryData.ToDictionary());
						break;
					}

					case ApiConfig.SystemStats.Mounts:
					{
						responseDictionary.Add("Mounts", DiskHandling.FullDisksData.ToDictionary());
						break;
					}

					case ApiConfig.SystemStats.Batteries:
					{
						responseDictionary.Add("Batteries", StatsApi.BatteryList.ToDictionary());
						break;
					}
					default:
					{
						throw new ArgumentOutOfRangeException(requestedStat.ToString());
					}
				}
			}

			yield return new SseItem<Dictionary<string, dynamic>>(responseDictionary, "StatUpdate");
			responseDictionary = [];

			await Task.Delay(delay, CancellationToken.None);
		}

		Logs.LogBook.Write(new (LogStream.Verbose, $"GetStatsSse [{streamId:X4}]", "Streaming has stopped."));
	}


	public static void MapStatsEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet($"{ApiConfig.BaseApiUrlPath}/stats/getCurrent", async (HttpResponse response, bool? meta, bool? system, bool? cpu, bool? gpu, bool? memory, bool? mounts, bool? batteries) =>
		{
			Dictionary<ApiConfig.SystemStats, bool?> requestedStatsRaw = new() {
				{ ApiConfig.SystemStats.Meta, meta },
				{ ApiConfig.SystemStats.System, system },
				{ ApiConfig.SystemStats.Cpu, cpu },
				{ ApiConfig.SystemStats.Gpu, gpu },
				{ ApiConfig.SystemStats.Memory, memory },
				{ ApiConfig.SystemStats.Mounts, mounts },
				{ ApiConfig.SystemStats.Batteries, batteries },
			};
			List<ApiConfig.SystemStats> requestedStats = [];
			if (requestedStatsRaw.All(stat => !stat.Value.HasValue))
			{
				requestedStats = Enum.GetValues<ApiConfig.SystemStats>().ToList();
			}
			else
			{
				requestedStats.AddRange(from statKvp in requestedStatsRaw where
					statKvp.Value.HasValue && statKvp.Value.Value select statKvp.Key);
			}

			if (requestedStats.Count == 0)
			{
				return Results.BadRequest("No stats were requested.");
			}
			Logs.LogBook.Write(new (LogStream.Verbose, "GetStats", $"Got a request for: {string.Join(", ", requestedStats.Select(stat => stat.ToString()))}"));

			// Request checks done

			Dictionary<string, dynamic> responseDictionary = [];

			// Queue and update all the requested stats up asynchronously
			List<Task> fetchTasks = [];
			foreach (var stat in requestedStats)
			{
				switch (stat)
				{
					case ApiConfig.SystemStats.Cpu:
					{
						fetchTasks.Add(Task.Run(StatsApi.CpuData.UpdateDataIfNecessary));
						break;
					}

					case ApiConfig.SystemStats.Gpu:
					{
						fetchTasks.Add(Task.Run(GpuHandling.FullGpusData.UpdateDataIfNecessary));
						break;
					}

					case ApiConfig.SystemStats.Memory:
					{
						fetchTasks.Add(Task.Run(StatsApi.MemoryData.UpdateDataIfNecessary));
						break;
					}

					case ApiConfig.SystemStats.Mounts:
					{
						fetchTasks.Add(Task.Run(DiskHandling.FullDisksData.UpdateDataIfNecessary));
						break;
					}
					case ApiConfig.SystemStats.Batteries:
					{
						fetchTasks.Add(Task.Run(StatsApi.BatteryList.UpdateDataIfNecessary));
						break;
					}
				}
			}
			await Task.WhenAll(fetchTasks);

			// Request assembly
			List <DateTime> lastUpdateStamps = [];
			foreach (var requestedStat in requestedStats)
			{
				switch (requestedStat)
				{
					case ApiConfig.SystemStats.Meta:
					{
						responseDictionary.Add("Meta",
							new Dictionary<string, dynamic?>
							{
								{ nameof(ApiConfig.Version), ApiConfig.Version },
								{ nameof(ApiConfig.ApiVersion), ApiConfig.ApiVersion },
								{ nameof(ApiConfig.ApiConfiguration.CacheLifetime),
									ApiConfig.ApiConfiguration.CacheLifetime },
								{ "SofaUptime", ApiConfig.SofaStartStopwatch.Elapsed }
							});
						break;
					}

					case ApiConfig.SystemStats.System:
					{
						responseDictionary.Add("System", StatsApi.GeneralSpecs.ToDictionary());
						break;
					}

					case ApiConfig.SystemStats.Cpu:
					{
						responseDictionary.Add("CPU", StatsApi.CpuData.ToDictionary());
						lastUpdateStamps.Add(StatsApi.CpuData.LastUpdate);
						break;
					}

					case ApiConfig.SystemStats.Gpu:
					{
						responseDictionary.Add("GPU", GpuHandling.FullGpusData.ToDictionary());
						lastUpdateStamps.Add(GpuHandling.FullGpusData.LastUpdate);
						break;
					}

					case ApiConfig.SystemStats.Memory:
					{
						responseDictionary.Add("Memory", StatsApi.MemoryData.ToDictionary());
						lastUpdateStamps.Add(StatsApi.MemoryData.LastUpdate);
						break;
					}

					case ApiConfig.SystemStats.Mounts:
					{
						responseDictionary.Add("Mounts", DiskHandling.FullDisksData.ToDictionary());
						lastUpdateStamps.Add(DiskHandling.FullDisksData.LastUpdate);
						break;
					}

					case ApiConfig.SystemStats.Batteries:
					{
						responseDictionary.Add("Batteries", StatsApi.BatteryList.ToDictionary());
						lastUpdateStamps.Add(StatsApi.BatteryList.LastUpdate);
						break;
					}
					default:
					{
						throw new ArgumentOutOfRangeException(requestedStat.ToString());
					}
				}
			}

			if (lastUpdateStamps.Count > 0)
			{
				var latestUpdate = lastUpdateStamps.Max().ToUniversalTime();
				response.Headers.Append("Expires", (latestUpdate + ApiConfig.ApiConfiguration.CacheLifetime)
					.ToString("R"));
			}
			await response.Body.WriteAsync( JsonSerializer.SerializeToUtf8Bytes(responseDictionary));
			await response.CompleteAsync();

			Logs.LogBook.Write(new (LogStream.Verbose, "GetStats", $"Sent off the response with {responseDictionary.Count} fields."));
			return Results.Empty;
		}).Produces(StatusCodes.Status200OK)
			.Produces(StatusCodes.Status400BadRequest)
			.WithSummary("Returns the stats/metrics of the server according to what was requested. Defaults to all in case none were specified.")
			.WithTags("Stats")
			.WithName("GetSystemMetrics")
			.RequireAuthorization("MultiAuth", "stats:get");

		app.MapGet($"{ApiConfig.BaseApiUrlPath}/stats/getStream", (HttpContext context, bool? meta, bool? system, bool? cpu, bool? gpu, bool? memory, bool? mounts, bool? batteries, ushort? delay, bool delayStrict = false) =>
		{
			Dictionary<ApiConfig.SystemStats, bool?> requestedStatsRaw = new() {
				{ ApiConfig.SystemStats.Meta, meta },
				{ ApiConfig.SystemStats.System, system },
				{ ApiConfig.SystemStats.Cpu, cpu },
				{ ApiConfig.SystemStats.Gpu, gpu },
				{ ApiConfig.SystemStats.Memory, memory },
				{ ApiConfig.SystemStats.Mounts, mounts },
				{ ApiConfig.SystemStats.Batteries, batteries }
			};
			List<ApiConfig.SystemStats> requestedStats = [];
			if (requestedStatsRaw.All(stat => !stat.Value.HasValue))
			{
				requestedStats = Enum.GetValues<ApiConfig.SystemStats>().ToList();
			}
			else
			{
				requestedStats.AddRange(from statKvp in requestedStatsRaw where
					statKvp.Value.HasValue && statKvp.Value.Value select statKvp.Key);
			}

			if (requestedStats.Count == 0)
			{
				return Results.BadRequest("No stats were requested.");
			}

			var streamId = (ushort) Random.Shared.Next(ushort.MaxValue);
			var delayActual = ApiConfig.ApiConfiguration.CacheLifetime.TotalSeconds == 0 ? TimeSpan.FromSeconds(1) : ApiConfig.ApiConfiguration.CacheLifetime;
			if (delay.HasValue)
			{
				if (delay.Value < delayActual.TotalSeconds)
				{
					if (delayStrict)
					{
						return Results.BadRequest("Delay was set less than the user-specified cache lifetime.");
					}
					Logs.LogBook.Write(new (LogStream.Verbose, $"GetStatsSse [{streamId:X4}]", $"Requested delay was {delay.Value} seconds, but the minimum cache lifetime is {delayActual.TotalSeconds}s."));
				}
				else
				{
					delayActual = TimeSpan.FromSeconds(delay.Value);
				}
			}

			context.Response.Headers.Append("X-Sofa-Lifetime", delayActual.TotalSeconds.ToString(CultureInfo.InvariantCulture));

			return Results.ServerSentEvents(StreamStats(requestedStats, delayActual, streamId, context.RequestAborted));
		}).Produces(StatusCodes.Status200OK)
			.Produces(StatusCodes.Status400BadRequest)
			.WithSummary("Returns a stream of stats as they update. Defaults to all in case none were specified.")
			.WithDescription("The delay parameter is optional and acts as a SUGGESTION to the server. If the user's set cache lifetime is higher than this, it will be ignored. Set delayStrict to return a 400 Bad Request in that case.\n" +
			                 "The final delay will be in the response header 'X-Sofa-Lifetime' in seconds regardless.")
			.WithTags("Stats")
			.WithName("GetSystemMetricsSse")
			.RequireAuthorization("MultiAuth", "stats:get");
	}
}