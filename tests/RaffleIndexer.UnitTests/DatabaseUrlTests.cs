using RaffleIndexer;

namespace RaffleIndexer.UnitTests;

public class DatabaseUrlTests
{
    // Render, Railway and Heroku all hand out a postgres:// URL. Npgsql only
    // understands key-value form, so the URL has to be translated at startup.
    [Fact]
    public void Converts_a_postgres_url_to_an_npgsql_connection_string()
    {
        var result = DatabaseUrl.Normalize("postgresql://indexer:s3cret@dpg-abc.oregon-postgres.render.com:5432/indexer_db");

        Assert.Contains("Host=dpg-abc.oregon-postgres.render.com", result);
        Assert.Contains("Port=5432", result);
        Assert.Contains("Database=indexer_db", result);
        Assert.Contains("Username=indexer", result);
        Assert.Contains("Password=s3cret", result);
    }

    [Fact]
    public void Accepts_the_shorter_postgres_scheme()
    {
        var result = DatabaseUrl.Normalize("postgres://u:p@db.example.com/mydb");

        Assert.Contains("Host=db.example.com", result);
        Assert.Contains("Database=mydb", result);
    }

    [Fact]
    public void Defaults_to_port_5432_when_the_url_omits_it()
    {
        var result = DatabaseUrl.Normalize("postgres://u:p@db.example.com/mydb");

        Assert.Contains("Port=5432", result);
    }

    [Fact]
    public void Decodes_percent_encoded_credentials()
    {
        // Generated passwords routinely contain characters that must be escaped.
        var result = DatabaseUrl.Normalize("postgres://u%40corp:p%40ss%2Fword@db.example.com/mydb");

        Assert.Contains("Username=u@corp", result);
        Assert.Contains("p@ss/word", result);
    }

    [Fact]
    public void Leaves_an_existing_npgsql_connection_string_untouched()
    {
        const string existing = "Host=db;Database=indexer;Username=indexer;Password=local";

        Assert.Equal(existing, DatabaseUrl.Normalize(existing));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Passes_through_empty_input_rather_than_throwing(string? input)
    {
        Assert.Equal(input ?? "", DatabaseUrl.Normalize(input));
    }
}
