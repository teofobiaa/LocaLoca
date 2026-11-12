using Npgsql;
using NpgsqlTypes;
using LocaLoca.Api.Security;

namespace LocaLoca.Api;

public static class ApiEndpointHelpers
{
    static async Task<NpgsqlConnection> OpenAsync(string? connectionString, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
            throw new DatabaseUnavailableException("PostgreSQL connection is not configured.");
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
    
    static async Task<IResult> SaveVisitAsync(string connectionString, VisitWriteRequest request, int userId, int? visitId, CancellationToken cancellationToken)
    {
        if (request.AttractionId <= 0) return Results.BadRequest(new ApiErrorResponse("Выберите достопримечательность."));
        if (!DateOnly.TryParse(request.VisitDate, out var date) || date > DateOnly.FromDateTime(DateTime.Today))
            return Results.BadRequest(new ApiErrorResponse("Укажите корректную дату посещения, не позднее сегодняшнего дня."));
        if (request.Rating is < 1 or > 5) return Results.BadRequest(new ApiErrorResponse("Оценка должна быть от 1 до 5."));
        int id;
        await using (var connection = await OpenAsync(connectionString, cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            if (visitId is null)
            {
                await using var insert = new NpgsqlCommand(@"INSERT INTO visits(user_id, attraction_id, visit_date, rating, impressions, notes)
                    VALUES (@u, @a, @d, @r, @i, @n) RETURNING visit_id", connection, transaction);
                AddVisitParameters(insert, userId, request, date);
                id = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));
            }
            else
            {
                await using var update = new NpgsqlCommand(@"UPDATE visits SET attraction_id=@a, visit_date=@d, rating=@r,
                    impressions=@i, notes=@n WHERE visit_id=@v AND user_id=@u", connection, transaction);
                AddVisitParameters(update, userId, request, date);
                update.Parameters.AddWithValue("v", NpgsqlDbType.Integer, visitId.Value);
                if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return Results.NotFound(new ApiErrorResponse("Посещение не найдено."));
                id = visitId.Value;
            }
            await transaction.CommitAsync(cancellationToken);
        }
        return Results.Ok(new VisitSavedDto(id, "Посещение сохранено."));
    }
    
    static void AddVisitParameters(NpgsqlCommand command, int userId, VisitWriteRequest request, DateOnly date)
    {
        command.Parameters.AddWithValue("u", NpgsqlDbType.Integer, userId);
        command.Parameters.AddWithValue("a", NpgsqlDbType.Integer, request.AttractionId);
        command.Parameters.AddWithValue("d", NpgsqlDbType.Date, date);
        command.Parameters.AddWithValue("r", NpgsqlDbType.Smallint, (short)request.Rating);
        command.Parameters.AddWithValue("i", NpgsqlDbType.Text, request.Impressions ?? string.Empty);
        command.Parameters.AddWithValue("n", NpgsqlDbType.Text, request.Notes ?? string.Empty);
    }
    
    static bool TryUser(HttpContext context, SessionStore sessions, out int userId)
    {
        userId = context.Items.TryGetValue("LocaLoca.UserId", out var value) && value is int id ? id : 0;
        return userId > 0;
    }
    
    static async Task<byte[]> ReadPhotoDataAsync(IFormFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var source = file.OpenReadStream();
        using var buffer = new MemoryStream(checked((int)file.Length));
        await source.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
    
    static string? SafePhotoPath(string dataRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var root = Path.GetFullPath(dataRoot + Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(dataRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
    
    static void TryDeletePhoto(string dataRoot, string relativePath)
    {
        try
        {
            var path = SafePhotoPath(dataRoot, relativePath);
            if (path is not null && File.Exists(path)) File.Delete(path);
        }
        catch { /* Файловая очистка не должна отменять уже выполненное удаление из БД. */ }
    }
}
