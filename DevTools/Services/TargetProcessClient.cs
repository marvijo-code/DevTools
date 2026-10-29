using System.Text;
using DevTools.Infrastructure;
using DevTools.Models;
using Microsoft.Data.SqlClient;

namespace DevTools.Services;

/// <summary>
/// Reads open work from a TargetProcess mirror database. The mirror is a read-only copy, so every
/// statement here is a SELECT and every table read takes WITH (NOLOCK) to stay off the sync writer's locks.
/// </summary>
public sealed class TargetProcessClient
{
    private const int CommandTimeoutSeconds = 30;

    /// <summary>
    /// One entry per TargetProcess entity family that can be assigned to a developer. The date column differs
    /// by family in the mirror (Requests use CreateDate, delivery entities use CreatedDate) but every one of
    /// them carries ModifyDate, which is what "oldest first" sorts on.
    /// </summary>
    private static readonly IReadOnlyList<(string Table, string EntityType)> AssignableEntities =
    [
        ("Requests", "Request"),
        ("UserStories", "UserStory"),
        ("Bugs", "Bug"),
        ("Tasks", "Task")
    ];

    private string? _connectionString;
    private string _portalBaseUrl = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connectionString);

    /// <summary>
    /// Root of the TargetProcess web portal, for example <c>https://example.tpondemand.com</c>. Used only to
    /// build "Open Ticket" links; the app works without it and simply disables those buttons.
    /// </summary>
    public string PortalBaseUrl => _portalBaseUrl;

    public void SetPortalBaseUrl(string? portalBaseUrl)
    {
        var trimmed = portalBaseUrl?.Trim().TrimEnd('/');
        _portalBaseUrl = string.IsNullOrWhiteSpace(trimmed) ? string.Empty : trimmed;
        AppLog.Info($"TargetProcess portal URL {(PortalBaseUrl.Length > 0 ? "applied" : "cleared")}.");
    }

    public void SetConnectionString(string? connectionString)
    {
        var trimmed = connectionString?.Trim();
        _connectionString = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        AppLog.Info($"TargetProcess connection string {(IsConfigured ? "applied" : "cleared")}.");
    }

    public void ClearConnectionString()
    {
        _connectionString = null;
        AppLog.Info("TargetProcess connection string cleared.");
    }

    /// <summary>
    /// Open tickets assigned to <paramref name="userId"/> as Developer, oldest touched first.
    /// A ticket also assigned to the same person as Developer Assistance is excluded: that role is broad
    /// triage rather than ownership, so counting it would swamp the list with work that is not theirs.
    /// </summary>
    public async Task<IReadOnlyList<TargetProcessTicketItem>> GetOpenTicketsAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "No TargetProcess connection string configured. Add one under Auth, Refresh, and Repository Setup.");
        }

        if (userId <= 0)
        {
            throw new InvalidOperationException(
                "No TargetProcess user id configured. Enter your name and click Resolve, or type the id directly.");
        }

        AppLog.Info($"GetOpenTicketsAsync started for TargetProcess user {userId}.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(BuildOpenTicketsQuery(), connection)
        {
            CommandTimeout = CommandTimeoutSeconds
        };
        command.Parameters.Add("@UserId", System.Data.SqlDbType.Int).Value = userId;

        var tickets = new List<TargetProcessTicketItem>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tickets.Add(new TargetProcessTicketItem
            {
                Id = reader.GetInt32(0),
                EntityType = reader.GetString(1),
                Name = ReadNullableString(reader, 2) ?? "Untitled",
                State = ReadNullableString(reader, 3) ?? "Unknown",
                Project = ReadNullableString(reader, 4) ?? "No project",
                ModifyDate = ReadDate(reader, 5),
                PortalBaseUrl = PortalBaseUrl
            });
        }

        AppLog.Info($"GetOpenTicketsAsync returned {tickets.Count} tickets for user {userId}.");
        return tickets;
    }

    /// <summary>
    /// Resolves a typed name fragment to candidate TargetProcess user ids, so nobody has to go digging in
    /// the TargetProcess UI for their own numeric id.
    /// </summary>
    public async Task<IReadOnlyList<(int Id, string FullName)>> FindUsersAsync(
        string nameFragment,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "No TargetProcess connection string configured. Add one under Auth, Refresh, and Repository Setup.");
        }

        var trimmed = nameFragment?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return [];
        }

        const string sql = """
            SELECT TOP 20 u.Id, LTRIM(RTRIM(ISNULL(u.FirstName, '') + ' ' + ISNULL(u.Surname, ''))) AS FullName
            FROM dbo.Users u WITH (NOLOCK)
            WHERE ISNULL(u.FirstName, '') + ' ' + ISNULL(u.Surname, '') LIKE @Search
            ORDER BY u.FirstName, u.Surname;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = CommandTimeoutSeconds
        };
        command.Parameters.Add("@Search", System.Data.SqlDbType.NVarChar, 200).Value = $"%{trimmed}%";

        var matches = new List<(int Id, string FullName)>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            matches.Add((reader.GetInt32(0), ReadNullableString(reader, 1) ?? $"User {reader.GetInt32(0)}"));
        }

        AppLog.Info($"FindUsersAsync matched {matches.Count} users for '{trimmed}'.");
        return matches;
    }

    /// <summary>
    /// Builds one SELECT per assignable entity family, UNION ALL'd together. The families cannot be queried
    /// as one table because each lives in its own mirror table with its own columns.
    /// </summary>
    private static string BuildOpenTicketsQuery()
    {
        var builder = new StringBuilder();

        for (var i = 0; i < AssignableEntities.Count; i++)
        {
            var (table, entityType) = AssignableEntities[i];

            if (i > 0)
            {
                builder.AppendLine().AppendLine("UNION ALL").AppendLine();
            }

            builder.AppendLine($"""
                SELECT e.Id,
                       '{entityType}' AS EntityType,
                       e.Name,
                       es.Name AS State,
                       p.Name AS ProjectName,
                       e.ModifyDate
                FROM dbo.{table} e WITH (NOLOCK)
                LEFT JOIN dbo.EntityStates es WITH (NOLOCK) ON es.Id = e.EntityStateId
                LEFT JOIN dbo.Projects p WITH (NOLOCK) ON p.Id = e.ProjectId
                WHERE EXISTS (
                          SELECT 1
                          FROM dbo.Assignments a WITH (NOLOCK)
                          INNER JOIN dbo.Roles ro WITH (NOLOCK) ON ro.Id = a.RoleId
                          WHERE a.EntityId = e.Id
                            AND a.UserId = @UserId
                            AND ro.Name = 'Developer'
                      )
                  AND NOT EXISTS (
                          SELECT 1
                          FROM dbo.Assignments a WITH (NOLOCK)
                          INNER JOIN dbo.Roles ro WITH (NOLOCK) ON ro.Id = a.RoleId
                          WHERE a.EntityId = e.Id
                            AND a.UserId = @UserId
                            AND ro.Name = 'Developer Assistance'
                      )
                  AND ISNULL(es.Name, '') <> 'Closed'
                  AND ISNULL(es.Name, '') <> 'Done'
                  AND ISNULL(es.Name, '') NOT LIKE '%Ready%Deployment%'
                """);
        }

        builder.AppendLine().Append("ORDER BY ModifyDate ASC;");
        return builder.ToString();
    }

    private static string? ReadNullableString(SqlDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset ReadDate(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return DateTimeOffset.MinValue;
        }

        var value = reader.GetDateTime(ordinal);
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local));
    }
}
