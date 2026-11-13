namespace LocaLoca.Api.Endpoints;

/// <summary>
/// Builds the map marker COUNT and page statements with viewport predicates
/// pushed directly onto each source table. Keeping the predicate shape
/// explicit lets PostgreSQL choose the coordinate indexes instead of planning
/// around a parameterized "no bounds OR viewport" expression.
/// </summary>
internal static class MapMarkerSqlBuilder
{
    internal static string BuildCountSql(bool hasBounds, bool crossesAntimeridian)
    {
        if (!hasBounds)
        {
            return @"
SELECT
    (SELECT count(*) FROM attractions) +
    (SELECT count(*) FROM user_map_places WHERE user_id = @u)";
        }

        var attractionFilter = BuildCoordinateFilter("a", crossesAntimeridian);
        var personalPlaceFilter = BuildCoordinateFilter("m", crossesAntimeridian);

        // COUNT deliberately avoids the visited/wished EXISTS projections:
        // those values are needed only for returned markers, never for totals.
        return $@"
SELECT
    (SELECT count(*) FROM attractions a WHERE {attractionFilter}) +
    (SELECT count(*) FROM user_map_places m
     WHERE m.user_id = @u AND {personalPlaceFilter})";
    }

    internal static string BuildPageSql(bool hasBounds, bool crossesAntimeridian)
    {
        var attractionFilter = hasBounds
            ? "WHERE " + BuildCoordinateFilter("a", crossesAntimeridian)
            : string.Empty;
        var personalPlaceFilter = hasBounds
            ? "AND " + BuildCoordinateFilter("m", crossesAntimeridian)
            : string.Empty;

        return $@"
SELECT a.attraction_id AS id, a.name, a.latitude, a.longitude,
       EXISTS(
           SELECT 1 FROM visits v
           WHERE v.user_id = @u AND v.attraction_id = a.attraction_id
       ) AS visited,
       EXISTS(
           SELECT 1 FROM wishlist w
           WHERE w.user_id = @u AND w.attraction_id = a.attraction_id
       ) AS wished,
       NULL::integer AS user_place_id,
       NULL::date AS visit_date,
       NULL::smallint AS rating,
       ''::text AS impressions,
       ''::text AS notes
FROM attractions a
{attractionFilter}

UNION ALL

SELECT m.user_map_place_id AS id, m.name, m.latitude, m.longitude,
       m.status = 'visited' AS visited,
       m.status = 'planned' AS wished,
       m.user_map_place_id AS user_place_id,
       m.visit_date,
       m.rating,
       m.impressions,
       m.notes
FROM user_map_places m
WHERE m.user_id = @u
{personalPlaceFilter}

-- user_place_id breaks ties when an attraction and a personal place share
-- both a name and an integer ID, keeping OFFSET pagination deterministic.
ORDER BY name, id, user_place_id NULLS FIRST
LIMIT @limit OFFSET @offset";
    }

    private static string BuildCoordinateFilter(string alias, bool crossesAntimeridian)
    {
        var longitudeFilter = crossesAntimeridian
            ? $"({alias}.longitude >= @west OR {alias}.longitude <= @east)"
            : $"{alias}.longitude BETWEEN @west AND @east";

        return $"{alias}.latitude BETWEEN @south AND @north AND {longitudeFilter}";
    }
}
