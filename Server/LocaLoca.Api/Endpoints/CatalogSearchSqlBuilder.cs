namespace LocaLoca.Api.Endpoints;

/// <summary>
/// Builds the catalog substring-search SQL. Trigram indexes become useful for
/// patterns with at least three characters; shorter searches keep the direct
/// predicate to avoid three independent scans without useful trigrams.
/// </summary>
public static class CatalogSearchSqlBuilder
{
    public const int TrigramMinimumCharacters = 3;

    public const string MatchingAttractionsCte = """
        WITH matching_attractions AS MATERIALIZED (
            SELECT attraction_id
            FROM public.attractions
            WHERE name ILIKE @pattern

            UNION

            SELECT attraction.attraction_id
            FROM public.cities AS city
            JOIN public.attractions AS attraction
              ON attraction.city_id = city.city_id
            WHERE city.name ILIKE @pattern

            UNION

            SELECT attraction.attraction_id
            FROM public.countries AS country
            JOIN public.cities AS city
              ON city.country_id = country.country_id
            JOIN public.attractions AS attraction
              ON attraction.city_id = city.city_id
            WHERE country.name ILIKE @pattern
        )
        """;

    public static bool UsesTrigramSearch(string normalizedSearch) =>
        normalizedSearch.Length >= TrigramMinimumCharacters;

    public static string BuildPredicate(string normalizedSearch)
    {
        if (normalizedSearch.Length == 0)
            return string.Empty;

        if (UsesTrigramSearch(normalizedSearch))
            return """
                   
                     AND a.attraction_id IN (
                         SELECT attraction_id FROM matching_attractions
                     )
                   """;

        return """
               
                 AND (
                     a.name ILIKE @pattern
                     OR ci.name ILIKE @pattern
                     OR co.name ILIKE @pattern
                 )
               """;
    }
}
