using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.OpenApi.Models;
using OpenAI.Chat;


public static class Program
{
	// Minimal request/response shapes
	public sealed record ChatMessage(string role, string content);
	public sealed record ChatRequest(string? conversationId, List<ChatMessage> messages, bool stream = false);

	public sealed record Citation(string id, string? title, string? url);
	public sealed record RetrievedChunk(string id, string content, double? score, Citation? citation);

	public static void Main(string[] args)
	{
		var builder = WebApplication.CreateBuilder(args);

		builder.Configuration
			.AddJsonFile("appsettings.json", optional: true)
			.AddEnvironmentVariables();

		var aoaiEndpoint = builder.Configuration["AzureOpenAI:Endpoint"];
		var aoaiKey = builder.Configuration["AzureOpenAI:ApiKey"];
		var aoaiDeployment = builder.Configuration["AzureOpenAI:Deployment"];

		var searchEndpoint = builder.Configuration["AzureSearch:Endpoint"];
		var searchKey = builder.Configuration["AzureSearch:ApiKey"];
		var searchIndex = builder.Configuration["AzureSearch:IndexName"];

		if (string.IsNullOrWhiteSpace(aoaiEndpoint) || string.IsNullOrWhiteSpace(aoaiKey) || string.IsNullOrWhiteSpace(aoaiDeployment))
		{
			builder.Logging.AddFilter("", LogLevel.Warning);
		}

		builder.Services.AddEndpointsApiExplorer();
		builder.Services.AddSwaggerGen(o =>
		{
			o.SwaggerDoc("v1", new OpenApiInfo { Title = "Chat API", Version = "v1" });

			// API key auth via header.
			o.AddSecurityDefinition(ApiKeyAuth.ApiKeySchemeName, new OpenApiSecurityScheme
			{
				Type = SecuritySchemeType.ApiKey,
				Name = ApiKeyAuth.ApiKeyHeaderName,
				In = ParameterLocation.Header,
				Description = "API key required. Provide in header as X-Api-Key."
			});
			o.AddSecurityRequirement(new OpenApiSecurityRequirement
			{
				{
					new OpenApiSecurityScheme
					{
						Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = ApiKeyAuth.ApiKeySchemeName }
					},
					Array.Empty<string>()
				}
			});
		});
		var apiKey = builder.Configuration["Auth:ApiKey"]; // optional; if set, requests must include header X-Api-Key

		builder.Services.AddSingleton(_ =>
		{
			if (string.IsNullOrWhiteSpace(aoaiEndpoint)) throw new InvalidOperationException("AzureOpenAI:Endpoint is required");
			return new AzureOpenAIClient(new Uri(aoaiEndpoint), new AzureKeyCredential(aoaiKey ?? throw new InvalidOperationException("AzureOpenAI:ApiKey is required")));
		});

		builder.Services.AddSingleton(_ =>
		{
			if (string.IsNullOrWhiteSpace(searchEndpoint)) throw new InvalidOperationException("AzureSearch:Endpoint is required");
			return new SearchClient(new Uri(searchEndpoint), searchIndex ?? throw new InvalidOperationException("AzureSearch:IndexName is required"), new AzureKeyCredential(searchKey ?? throw new InvalidOperationException("AzureSearch:ApiKey is required")));
		});

		var app = builder.Build();

		app.UseSwagger();
		app.UseSwaggerUI();

		if (!string.IsNullOrWhiteSpace(apiKey))
		{
			app.Use(async (ctx, next) =>
			{
				var p = ctx.Request.Path;
				if (p.StartsWithSegments("/swagger") || p == "/")
				{
					await next(ctx);
					return;
				}

				if (!ctx.Request.Headers.TryGetValue(ApiKeyAuth.ApiKeyHeaderName, out var provided) ||
					!string.Equals(provided.ToString(), apiKey, StringComparison.Ordinal))
				{
					ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
					await ctx.Response.WriteAsJsonAsync(new { error = "Unauthorized" }, ctx.RequestAborted);
					return;
				}

				await next(ctx);
			});
		}

		app.MapPost("/chat", async (ChatRequest request, AzureOpenAIClient openAiClient, SearchClient searchClient, CancellationToken ct) =>
		{
			if (request.stream)
			{
				return Results.BadRequest(new { error = "Streaming is not implemented." });
			}

			if (request.messages.Count == 0)
			{
				return Results.BadRequest(new { error = "At least one message is required." });
			}

			var query = request.messages.LastOrDefault(m => string.Equals(m.role, "user", StringComparison.OrdinalIgnoreCase))?.content;
			if (string.IsNullOrWhiteSpace(query))
			{
				return Results.BadRequest(new { error = "A user message is required for retrieval." });
			}

			var retrievedChunks = await RetrieveChunksAsync(searchClient, query, ct);

			var messages = new List<OpenAI.Chat.ChatMessage>();
			if (retrievedChunks.Count > 0)
			{
				messages.Add(new SystemChatMessage(BuildContextPrompt(retrievedChunks)));
			}

			foreach (var message in request.messages)
			{
				messages.Add(MapToAzureMessage(message));
			}

			var chatClient = openAiClient.GetChatClient(aoaiDeployment ?? throw new InvalidOperationException("AzureOpenAI:Deployment is required"));
			var completion = await chatClient.CompleteChatAsync(messages, cancellationToken: ct);
			var responseText = completion.Value.Content?.ToString() ?? string.Empty;

			return Results.Ok(new
			{
				conversationId = request.conversationId ?? Guid.NewGuid().ToString("N"),
				message = responseText,
				retrievedChunks
			});
		});

		app.MapGet("/", () => Results.Ok(new { status = "ok" }));
		app.Run();
	}

	private static OpenAI.Chat.ChatMessage MapToAzureMessage(ChatMessage message)
	{
		return message.role.ToLowerInvariant() switch
		{
			"system" => new SystemChatMessage(message.content),
			"assistant" => new AssistantChatMessage(message.content),
			_ => new UserChatMessage(message.content)
		};
	}

	private static string BuildContextPrompt(List<RetrievedChunk> chunks)
	{
		var builder = new StringBuilder();
		builder.AppendLine("Use the following sources to answer the user. Cite sources as [source:N].");

		for (var i = 0; i < chunks.Count; i++)
		{
			builder.AppendLine();
			builder.Append("[source:");
			builder.Append(i + 1);
			builder.AppendLine("]");
			builder.AppendLine(chunks[i].content);
		}

		return builder.ToString();
	}

	private static async Task<List<RetrievedChunk>> RetrieveChunksAsync(SearchClient searchClient, string query, CancellationToken ct)
	{
		var options = new SearchOptions
		{
			Size = 5
		};

		var response = await searchClient.SearchAsync<SearchDocument>(query, options, ct);
		var results = new List<RetrievedChunk>();

		await foreach (var result in response.Value.GetResultsAsync())
		{
			var doc = result.Document;
			var id = GetString(doc, "id", "chunk_id", "key") ?? Guid.NewGuid().ToString("N");
			var content = GetString(doc, "content", "text", "chunk") ?? string.Empty;
			var title = GetString(doc, "title");
			var url = GetString(doc, "url");
			var citation = title is null && url is null ? null : new Citation(id, title, url);

			results.Add(new RetrievedChunk(id, content, result.Score, citation));
		}

		return results;
	}

	private static string? GetString(SearchDocument doc, params string[] keys)
	{
		foreach (var key in keys)
		{
			if (doc.TryGetValue(key, out var value) && value is not null)
			{
				return Convert.ToString(value, CultureInfo.InvariantCulture);
			}
		}

		return null;
	}

	private static class ApiKeyAuth
	{
		public const string ApiKeyHeaderName = "X-Api-Key";
		public const string ApiKeySchemeName = "ApiKey";
	}
}
