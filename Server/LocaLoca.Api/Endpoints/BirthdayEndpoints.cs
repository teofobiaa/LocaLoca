using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using LocaLoca.Api;
using LocaLoca.Api.Security;
using static LocaLoca.Api.ApiEndpointHelpers;

namespace LocaLoca.Api.Endpoints;

public static class BirthdayEndpoints
{
    private static readonly DateOnly EarliestBirthDate = new(1900, 1, 1);

    public static void MapBirthdayEndpoints(this WebApplication app, string? connectionString)
    {
        // The full date of birth is returned only by this owner-authenticated endpoint.
        // It is intentionally absent from UserDto, authentication responses and public routes.
        app.MapGet("/api/me/profile/birthday", async Task<IResult> (
            HttpContext context,
            SessionStore sessions,
            CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();

            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT date_of_birth FROM public.users WHERE user_id = @user", connection);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is null) return Results.NotFound(new ApiErrorResponse("Аккаунт не найден."));

            return Results.Ok(new PrivateBirthDateDto(value is DBNull ? null : DateOnly.FromDateTime((DateTime)value)));
        });

        app.MapPut("/api/me/profile/birthday", async Task<IResult> (
            UpdateBirthDateRequest request,
            HttpContext context,
            SessionStore sessions,
            CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (request.DateOfBirth is { } date &&
                (date < EarliestBirthDate || date > today))
            {
                return Results.BadRequest(new ApiErrorResponse(
                    $"Дата рождения должна находиться между {EarliestBirthDate:yyyy-MM-dd} и сегодняшним днём."));
            }

            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                UPDATE public.users
                SET date_of_birth = @birth_date
                WHERE user_id = @user
                RETURNING date_of_birth", connection);
            command.Parameters.Add("birth_date", NpgsqlDbType.Date).Value =
                request.DateOfBirth is { } birthDate ? birthDate : DBNull.Value;
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);

            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is null) return Results.NotFound(new ApiErrorResponse("Аккаунт не найден."));

            return Results.Ok(new PrivateBirthDateDto(
                value is DBNull ? null : DateOnly.FromDateTime((DateTime)value)));
        });

        app.MapGet("/api/me/birthday-contacts", async Task<IResult> (
            DateOnly? today,
            HttpContext context,
            SessionStore sessions,
            CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();

            var referenceDate = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var contacts = new List<BirthdayContactDto>();
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                SELECT birthday_contact_id, display_name, birth_date
                FROM public.birthday_contacts
                WHERE user_id = @user
                ORDER BY birth_date, display_name, birthday_contact_id", connection);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var birthDate = DateOnly.FromDateTime(reader.GetDateTime(2));
                contacts.Add(CreateContactDto(
                    reader.GetInt32(0), reader.GetString(1), birthDate, referenceDate));
            }

            contacts.Sort(static (left, right) =>
            {
                var byDate = left.DaysUntilBirthday.CompareTo(right.DaysUntilBirthday);
                return byDate != 0 ? byDate : string.Compare(left.DisplayName, right.DisplayName, StringComparison.CurrentCulture);
            });

            return Results.Ok(new ItemsResponse<BirthdayContactDto>(contacts));
        });

        app.MapPost("/api/me/birthday-contacts", async Task<IResult> (
            BirthdayContactWriteRequest request,
            HttpContext context,
            SessionStore sessions,
            CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();

            var validation = ValidateContact(request.DisplayName, request.BirthDate);
            if (validation is not null) return Results.BadRequest(new ApiErrorResponse(validation));

            var displayName = request.DisplayName.Trim();
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                INSERT INTO public.birthday_contacts (user_id, display_name, birth_date)
                VALUES (@user, @name, @birth_date)
                RETURNING birthday_contact_id", connection);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);
            command.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, displayName);
            command.Parameters.Add("birth_date", NpgsqlDbType.Date).Value = request.BirthDate;

            var id = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            return Results.Ok(CreateContactDto(
                id, displayName, request.BirthDate, DateOnly.FromDateTime(DateTime.UtcNow)));
        });

        app.MapPut("/api/me/birthday-contacts/{contactId:int}", async Task<IResult> (
            int contactId,
            BirthdayContactWriteRequest request,
            HttpContext context,
            SessionStore sessions,
            CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
            if (contactId <= 0) return Results.BadRequest(new ApiErrorResponse("Некорректный идентификатор контакта."));

            var validation = ValidateContact(request.DisplayName, request.BirthDate);
            if (validation is not null) return Results.BadRequest(new ApiErrorResponse(validation));

            var displayName = request.DisplayName.Trim();
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                UPDATE public.birthday_contacts
                SET display_name = @name, birth_date = @birth_date, updated_at = now()
                WHERE birthday_contact_id = @contact AND user_id = @user
                RETURNING birthday_contact_id", connection);
            command.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, displayName);
            command.Parameters.Add("birth_date", NpgsqlDbType.Date).Value = request.BirthDate;
            command.Parameters.AddWithValue("contact", NpgsqlDbType.Integer, contactId);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);

            var updatedId = await command.ExecuteScalarAsync(cancellationToken);
            if (updatedId is null) return Results.NotFound(new ApiErrorResponse("Контакт не найден."));
            return Results.Ok(CreateContactDto(
                Convert.ToInt32(updatedId), displayName, request.BirthDate, DateOnly.FromDateTime(DateTime.UtcNow)));
        });

        app.MapDelete("/api/me/birthday-contacts/{contactId:int}", async Task<IResult> (
            int contactId,
            HttpContext context,
            SessionStore sessions,
            CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
            if (contactId <= 0) return Results.BadRequest(new ApiErrorResponse("Некорректный идентификатор контакта."));

            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                DELETE FROM public.birthday_contacts
                WHERE birthday_contact_id = @contact AND user_id = @user", connection);
            command.Parameters.AddWithValue("contact", NpgsqlDbType.Integer, contactId);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);

            return await command.ExecuteNonQueryAsync(cancellationToken) == 0
                ? Results.NotFound(new ApiErrorResponse("Контакт не найден."))
                : Results.Ok(new MessageDto("День рождения удалён из списка."));
        });

        app.MapGet("/api/me/birthday-contacts/{contactId:int}/calendar.ics", async Task<IResult> (
            int contactId,
            HttpContext context,
            SessionStore sessions,
            CancellationToken cancellationToken) =>
        {
            if (!TryUser(context, sessions, out var userId)) return Results.Unauthorized();
            if (contactId <= 0) return Results.BadRequest(new ApiErrorResponse("Некорректный идентификатор контакта."));

            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await using var command = new NpgsqlCommand(@"
                SELECT display_name, birth_date
                FROM public.birthday_contacts
                WHERE birthday_contact_id = @contact AND user_id = @user", connection);
            command.Parameters.AddWithValue("contact", NpgsqlDbType.Integer, contactId);
            command.Parameters.AddWithValue("user", NpgsqlDbType.Integer, userId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return Results.NotFound(new ApiErrorResponse("Контакт не найден."));

            var displayName = reader.GetString(0);
            var birthDate = DateOnly.FromDateTime(reader.GetDateTime(1));
            var calendar = BuildCalendarEvent(contactId, displayName, birthDate);
            context.Response.Headers["Content-Disposition"] =
                $"attachment; filename=\"birthday-{contactId.ToString(CultureInfo.InvariantCulture)}.ics\"";
            return Results.Text(calendar, "text/calendar; charset=utf-8");
        });
    }

    private static string? ValidateContact(string? displayName, DateOnly birthDate)
    {
        var normalizedName = displayName?.Trim() ?? string.Empty;
        if (normalizedName.Length is < 1 or > 80 || normalizedName.Any(char.IsControl))
            return "Имя контакта должно содержать от 1 до 80 символов и не содержать управляющих символов.";

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (birthDate < EarliestBirthDate || birthDate > today)
            return $"Дата рождения должна находиться между {EarliestBirthDate:yyyy-MM-dd} и сегодняшним днём.";

        return null;
    }

    private static BirthdayContactDto CreateContactDto(
        int id, string displayName, DateOnly birthDate, DateOnly today)
    {
        var nextBirthday = GetNextBirthday(birthDate, today);
        return new BirthdayContactDto(
            id,
            displayName,
            birthDate,
            nextBirthday,
            nextBirthday.DayNumber - today.DayNumber,
            nextBirthday.Year - birthDate.Year);
    }

    private static DateOnly GetNextBirthday(DateOnly birthDate, DateOnly today)
    {
        var candidate = BirthdayInYear(birthDate, today.Year);
        return candidate < today ? BirthdayInYear(birthDate, today.Year + 1) : candidate;
    }

    // February 29 birthdays are observed on February 28 in non-leap years.
    private static DateOnly BirthdayInYear(DateOnly birthDate, int year) =>
        birthDate.Month == 2 && birthDate.Day == 29 && !DateTime.IsLeapYear(year)
            ? new DateOnly(year, 2, 28)
            : new DateOnly(year, birthDate.Month, birthDate.Day);

    private static string BuildCalendarEvent(int contactId, string displayName, DateOnly birthDate)
    {
        var now = DateTime.UtcNow;
        var uid = $"localoca-birthday-{contactId}-{Guid.NewGuid():N}@localoca";
        var summary = EscapeCalendarText($"{displayName} — день рождения");
        var description = EscapeCalendarText($"Поздравь {displayName} с днём рождения.");
        var lines = new[]
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//LocaLoca//Birthday Reminders//RU",
            "CALSCALE:GREGORIAN",
            "BEGIN:VEVENT",
            $"UID:{uid}",
            $"DTSTAMP:{now:yyyyMMdd'T'HHmmss'Z'}",
            $"DTSTART;VALUE=DATE:{birthDate:yyyyMMdd}",
            $"DTEND;VALUE=DATE:{birthDate.AddDays(1):yyyyMMdd}",
            "RRULE:FREQ=YEARLY",
            $"SUMMARY:{summary}",
            $"DESCRIPTION:{description}",
            "BEGIN:VALARM",
            "ACTION:DISPLAY",
            "TRIGGER:-P1D",
            $"DESCRIPTION:{EscapeCalendarText($"Завтра день рождения: {displayName}")}",
            "END:VALARM",
            "END:VEVENT",
            "END:VCALENDAR"
        };

        return string.Join("\r\n", lines.Select(FoldCalendarLine)) + "\r\n";
    }

    private static string EscapeCalendarText(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal);

    private static string FoldCalendarLine(string line)
    {
        var builder = new StringBuilder(line.Length + 8);
        var currentBytes = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            if (currentBytes + rune.Utf8SequenceLength > 75)
            {
                builder.Append("\r\n ");
                currentBytes = 1;
            }

            builder.Append(rune.ToString());
            currentBytes += rune.Utf8SequenceLength;
        }

        return builder.ToString();
    }
}
