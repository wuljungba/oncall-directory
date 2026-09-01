using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Data;
using OnCallApi.Services;
using OnCallApi.Services.Import;

namespace BackendTests.Services;

/// <summary>
/// A real staff export writes one "Name" column and expects the reader to work it out.
/// The importer used to demand two columns and failed every such row with "firstName and
/// lastName are required" -- a message about the data, for a formatting problem.
///
/// The point of these is not that every name parses. It is that the parser knows which
/// ones it has actually understood: filing someone under the wrong half of their name is
/// how a directory loses a person, and the failure is silent until somebody needs them.
/// </summary>
public class NameParserTests
{
    // ── "Last, First" is the strongest signal there is ──

    [Theory]
    [InlineData("Doe, John", "John", "Doe")]
    [InlineData("Smith, Jane", "Jane", "Smith")]
    [InlineData("O'Brien, Patrick", "Patrick", "O'Brien")]
    [InlineData("van der Berg, Johannes", "Johannes", "van der Berg")]
    public void CommaMeansSurnameFirst(string input, string first, string last)
    {
        var parsed = NameParser.Parse(input);

        parsed.FirstName.Should().Be(first);
        parsed.LastName.Should().Be(last);
        parsed.Confidence.Should().Be(NameConfidence.High);
    }

    /// <summary>
    /// The ordering trap: "Jane Smith, MD" and "Smith, Jane" both hold exactly one comma
    /// and mean opposite things. Reading the comma before removing the credential files
    /// Jane under the surname "Jane Smith".
    /// </summary>
    [Theory]
    [InlineData("Jane Smith, MD", "Jane", "Smith", "MD")]
    [InlineData("Smith, Jane, MD", "Jane", "Smith", "MD")]
    [InlineData("Patrick O'Brien, PA-C", "Patrick", "O'Brien", "PA-C")]
    [InlineData("Smith, Jane, RN, BSN", "Jane", "Smith", "RN, BSN")]
    public void CredentialsComeOffBeforeTheCommaIsRead(
        string input, string first, string last, string credentials)
    {
        var parsed = NameParser.Parse(input);

        parsed.FirstName.Should().Be(first);
        parsed.LastName.Should().Be(last);
        parsed.Credentials.Should().Be(credentials);
    }

    // ── Titles and suffixes ──

    [Theory]
    [InlineData("Dr. Jane Smith", "Jane", "Smith")]
    [InlineData("Mrs. Jane Smith", "Jane", "Smith")]
    [InlineData("Dr Jane Smith", "Jane", "Smith")]
    public void LeadingTitlesAreRemoved(string input, string first, string last)
    {
        var parsed = NameParser.Parse(input);

        parsed.FirstName.Should().Be(first);
        parsed.LastName.Should().Be(last);
    }

    [Theory]
    [InlineData("John Doe Jr.", "John", "Doe", "Jr")]
    [InlineData("John Doe III", "John", "Doe", "III")]
    [InlineData("Doe, John, Sr.", "John", "Doe", "Sr")]
    public void SuffixesBelongToNeitherName(string input, string first, string last, string suffix)
    {
        var parsed = NameParser.Parse(input);

        parsed.FirstName.Should().Be(first);
        parsed.LastName.Should().Be(last);
        parsed.Suffix.Should().Be(suffix);
    }

    // ── Particles keep the surname whole ──

    [Theory]
    [InlineData("John van der Berg", "John", "van der Berg")]
    [InlineData("Maria de la Cruz", "Maria", "de la Cruz")]
    [InlineData("Ludwig von Beethoven", "Ludwig", "von Beethoven")]
    [InlineData("Ahmed bin Rashid", "Ahmed", "bin Rashid")]
    public void AParticleMarksWhereTheSurnameStarts(string input, string first, string last)
    {
        var parsed = NameParser.Parse(input);

        parsed.FirstName.Should().Be(first);
        parsed.LastName.Should().Be(last);
        parsed.Confidence.Should().Be(NameConfidence.High,
            "a particle says where the surname begins as clearly as a comma does");
    }

    // ── Plain cases ──

    [Fact]
    public void TwoWordsAreForenameAndSurname()
    {
        var parsed = NameParser.Parse("Jane Smith");

        parsed.FirstName.Should().Be("Jane");
        parsed.LastName.Should().Be("Smith");
        parsed.Confidence.Should().Be(NameConfidence.High);
    }

    [Fact]
    public void ThreeWordsAreReadAsAMiddleNameButOnlyAsAGuess()
    {
        var parsed = NameParser.Parse("Jane Marie Smith");

        parsed.FirstName.Should().Be("Jane");
        parsed.MiddleName.Should().Be("Marie");
        parsed.LastName.Should().Be("Smith");
        parsed.Confidence.Should().Be(NameConfidence.Medium,
            "a two-word surname would break this reading, so it is not asserted as certain");
    }

    // ── What it refuses to guess ──

    [Theory]
    [InlineData("3North")]
    [InlineData("Cardiology")]
    [InlineData("Smith")]
    public void ASingleWordIsNotAName(string input)
    {
        var parsed = NameParser.Parse(input);

        parsed.Confidence.Should().Be(NameConfidence.Low);
        parsed.FirstName.Should().BeEmpty();
        parsed.LastName.Should().BeEmpty();
        parsed.DisplayName.Should().Be(input, "the original must survive for the caller to use");
        parsed.ReviewReason.Should().NotBeNull();
    }

    [Fact]
    public void FourWordsWithNoCommaAndNoParticleIsAGuessNotAnAnswer()
    {
        var parsed = NameParser.Parse("Maria Elena Rodriguez Garcia");

        parsed.Confidence.Should().Be(NameConfidence.Low,
            "Rodriguez Garcia may be one surname or two, and only a person knows which");
        parsed.ReviewReason.Should().NotBeNull();
    }

    [Fact]
    public void AnEmptyValueIsLowConfidenceRatherThanAnException()
    {
        NameParser.Parse(null).Confidence.Should().Be(NameConfidence.Low);
        NameParser.Parse("   ").Confidence.Should().Be(NameConfidence.Low);
        NameParser.Parse("Dr.").Confidence.Should().Be(NameConfidence.Low);
    }

    // ── Casing ──

    [Theory]
    [InlineData("SMITH, JANE", "Jane", "Smith")]
    [InlineData("MCDONALD, ANGUS", "Angus", "McDonald")]
    [InlineData("O'BRIEN, PATRICK", "Patrick", "O'Brien")]
    [InlineData("VAN DER BERG, JOHANNES", "Johannes", "van der Berg")]
    [InlineData("SMITH-JONES, JANE", "Jane", "Smith-Jones")]
    public void AShoutedNameIsRestored(string input, string first, string last)
    {
        var parsed = NameParser.Parse(input);

        parsed.FirstName.Should().Be(first);
        parsed.LastName.Should().Be(last);
    }

    /// <summary>
    /// Whoever typed "McDonald" or "van der Berg" knew what they meant. Re-casing a name
    /// that was already correct is a way to get it wrong.
    /// </summary>
    [Theory]
    [InlineData("McDonald, Angus", "Angus", "McDonald")]
    [InlineData("van der Berg, Johannes", "Johannes", "van der Berg")]
    [InlineData("de la Cruz, Maria", "Maria", "de la Cruz")]
    public void AMixedCaseNameIsLeftExactlyAsItWasWritten(string input, string first, string last)
    {
        var parsed = NameParser.Parse(input);

        parsed.FirstName.Should().Be(first);
        parsed.LastName.Should().Be(last);
    }

    // ── Through the importer ──

    private static BulkImportService CreateService(AppDbContext db)
        => new(db, NullLogger<BulkImportService>.Instance);

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task ImportsAFileWithOneCombinedNameColumn()
    {
        using var db = CreateDb();

        var csv = "name,email,officePhone\n"
                + "\"Doe, John, MD\",john.doe@hospital.example,(202) 555-0134\n"
                + "Dr. Jane Smith,jane.smith@hospital.example,(202) 555-0135";

        var result = await CreateService(db).ImportEmployeesAsync(ToStream(csv));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));

        var john = await db.Employees.AsNoTracking().SingleAsync(e => e.LastName == "Doe");
        john.FirstName.Should().Be("John");
        john.Credentials.Should().Be("MD");

        var jane = await db.Employees.AsNoTracking().SingleAsync(e => e.LastName == "Smith");
        jane.FirstName.Should().Be("Jane");
    }

    [Fact]
    public async Task SeparateColumnsAlwaysBeatTheParser()
    {
        using var db = CreateDb();

        // The combined column disagrees with the explicit ones. Being told beats parsing.
        var csv = "name,firstName,lastName,email\n"
                + "Wrong Person,Jane,Smith,jane@hospital.example";

        var result = await CreateService(db).ImportEmployeesAsync(ToStream(csv));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));

        var employee = await db.Employees.AsNoTracking().SingleAsync();
        employee.FirstName.Should().Be("Jane");
        employee.LastName.Should().Be("Smith");
    }

    [Fact]
    public async Task AnUnparseableNameIsReportedAsItselfNotAsAMissingColumn()
    {
        using var db = CreateDb();

        var csv = "name,email\n"
                + "Maria Elena Rodriguez Garcia,maria@hospital.example";

        var result = await CreateService(db).ImportEmployeesAsync(ToStream(csv));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Rodriguez Garcia") || e.Contains("Maria"));
        result.Errors.Should().NotContain(e => e == "Row 2: firstName and lastName are required.");
    }

    /// <summary>
    /// The two features meeting: a single-word name with no email and a phone number is a
    /// unit, and the department-contact branch claims it rather than reporting a name the
    /// parser could not read.
    /// </summary>
    [Fact]
    public async Task ASingleWordNameWithNoEmailBecomesAUnit()
    {
        using var db = CreateDb();

        var csv = "name,officePhone,extension\n"
                + "3North,845-568-3434,x3434";

        var result = await CreateService(db).ImportEmployeesAsync(ToStream(csv));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));

        var contact = await db.Employees.AsNoTracking().SingleAsync();
        contact.ContactType.Should().Be(OnCallApi.Models.ContactType.Department);
        contact.DisplayName.Should().Be("3North");
        contact.Extension.Should().Be("3434");
    }

    private static MemoryStream ToStream(string csv)
    {
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        writer.Write(csv);
        writer.Flush();
        stream.Position = 0;
        return stream;
    }
}
