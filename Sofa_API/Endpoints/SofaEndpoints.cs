using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Sofa_API.Endpoints;

public static class SofaEndpoints
{
	private static async IAsyncEnumerable<SseItem<string>> StreamSofaLogs(LogStream verbosity, byte initialContext, ushort streamId, string format, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		Queue<LogEntry> bufferedLogs = [];
		if (initialContext > 0)
		{
			lock (Logs.LogBook.BookWriteLock)
			{
				Logs.StreamWrittenTo += EnqueueLogs;

				var pastLogs = File.ReadAllLines(Path.Combine(SofaPaths.SubPaths.PathToLogFolder,
					$"[{DateOnly.FromDateTime(DateTime.Now).ToString("O")}] Sofa.log")).Reverse();

				var collectedContext = new List<LogEntry>(initialContext);
				foreach (var pastLogEntry in pastLogs)
				{
					// If it's a header, skip it
					if (pastLogEntry.StartsWith("Time Written") || pastLogEntry.StartsWith("========"))
					{
						continue;
					}

					LogEntry parsedLog;
					try
					{
						parsedLog = LogEntry.Parse(pastLogEntry);
					}
					catch {
						// skip malformed logs
						continue;
					}
					if (parsedLog.LogStream > verbosity) continue;
					collectedContext.Add(parsedLog);

					if (collectedContext.Count >= initialContext) break;
				}

				collectedContext.Reverse();
				foreach (var logEntry in collectedContext)
				{
					bufferedLogs.Enqueue(logEntry);
				}
			}
		}
		else
		{
			Logs.StreamWrittenTo += EnqueueLogs;
		}
		Logs.LogBook.Write(new (LogStream.Verbose, $"StreamSofaLogs [{streamId:X4}]", $"Streaming Sofa logs up to {verbosity} streams and an initial context of {initialContext} lines."));

		while (!cancellationToken.IsCancellationRequested)
		{
			if (bufferedLogs.Count > 0)
			{
				string logEntry = format switch
				{
					"json" => JsonSerializer.Serialize(bufferedLogs.Dequeue().ToDictionary()),
					"binary" => Convert.ToBase64String(bufferedLogs.Dequeue().Serialize()),
					_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Invalid format specified.")
				};

				yield return new SseItem<string>(logEntry, "newLogEntry");
			}
			else
			{
				await Task.Delay(500, CancellationToken.None);
			}
		}
		Logs.StreamWrittenTo -= EnqueueLogs;
		Logs.LogBook.Write(new(LogStream.Verbose, $"StreamSofaLogs [{streamId:X4}]", "Log streaming has stopped."));
		yield break;

		void EnqueueLogs(object? e, LogEntry logEntry)
		{
			// Only enqueue logs following the verbosity level, but also force any logs regarding this specific stream to be included.
			if (logEntry.LogStream <= verbosity || logEntry.ModuleName == $"StreamSofaLogs [{streamId:X4}]")
				bufferedLogs.Enqueue(logEntry);
		}
	}


	public static void MapSofaEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet($"{ApiConfig.BaseApiUrlPath}/logs/getStream", (HttpContext context, CancellationToken cancellationToken, int? verbosity, string format = "json", byte initialContext = 50) =>
		{
			if (verbosity == 0) return Results.BadRequest(new { message = "Verbosity cannot be 0." });
			if (format is not ("json" or "binary")) return Results.BadRequest(new { message = "Invalid format specified.", validFormats = (string[]) ["json", "binary"] });

			initialContext = byte.Clamp(initialContext, 0, 100);
			if (verbosity is not null) verbosity = int.Clamp(verbosity.Value, 1, byte.MaxValue);
			var verbosityEnum = ParsingMethods.ClampToMaxLogStreamValue((byte?) verbosity) ?? ApiConfig.ApiConfiguration.LogVerbosity;
			var streamId = (ushort) Random.Shared.Next(0, ushort.MaxValue);

			context.Response.Headers.Append("X-Stream-Id", streamId.ToString());
			context.Response.Headers.Append("X-Verbosity", $"{(byte) verbosityEnum}");
			context.Response.Headers.Append("X-Context-Included", $"{initialContext}");


			return Results.ServerSentEvents(StreamSofaLogs(verbosityEnum, initialContext, streamId, format, cancellationToken: cancellationToken));
		}).Produces(StatusCodes.Status200OK)
			.Produces(StatusCodes.Status400BadRequest)
			.WithSummary("Returns a stream of Sofa's logs.")
			.WithDescription("`verbosity` is optional and specifies the level of log verbosity to follow. Defaults to Sofa's configured `LogVerbosity` setting. " +
			                 "`initialContext` is optional and referres to how many lines of past logs to include at the start of the stream. Defaults to 50 [Range: 0-100]. " +
			                 "`format` can be set to either 'json' or 'binary', meaning either JSON entries for each log, or a Base64 encoded string of the serialized log entry.")
			.WithTags("Sofa", "Logs")
			.WithName("GetSofaLogsSse")
			.RequireAuthorization("MultiAuth", "logs:view");

		app.MapGet("/about", () => Results.Json(new
		{
			InstanceName = ApiConfig.ApiConfiguration.BackendName,
			InstanceId = ApiConfig.InstanceId,
			Version = ApiConfig.Version,
			BasePath = ApiConfig.BaseApiUrlPath,
			ClientRequestLifetime = ApiConfig.ApiConfiguration.ClientRequestLifetime
		})).Produces(StatusCodes.Status200OK)
		.WithSummary("Returns information about the Sofa instance. Useful for first contact.")
		.WithTags("Sofa", "About")
		.WithName("SofaAbout");
	}
}