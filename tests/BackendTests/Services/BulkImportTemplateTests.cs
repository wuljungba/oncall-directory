using FluentAssertions;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// The template we hand people has to name columns this parser actually understands.
///
/// It drifted: the downloaded template offered <c>departmentId</c> — an internal integer
/// nobody can supply without reading the database — and omitted <c>department</c>, which
/// takes the name and is what <c>ResolveDepartmentNamesAsync</c> resolves. Since an
/// unrecognised department fails the *entire* import, the template was steering people
/// into the one error that rejects their whole file.
///
/// The column list below is deliberately a copy of the frontend's TEMPLATE_COLUMNS
/// (src/frontend/src/utils/importFields.ts). That duplication is the point: changing the
/// template without checking it against the parser breaks this test.
/// </summary>
public class BulkImportTemplateTests
{
    /// <summary>Keep in step with TEMPLATE_COLUMNS in src/frontend/src/utils/importFields.ts.</summary>
    private static readonly string[] TemplateColumns =
    [
        "firstName", "lastName", "displayName", "email", "title", "credentials",
        "officePhone", "mobilePhone", "extension", "officeLocation",
        "department", "departmentId", "contactType", "azureAdObjectId",
    ];

    /// <summary>
    /// The canonical names <see cref="BulkImportService.ParseEmployeeRow"/> reads out of a
    /// row. Anything else in a file is ignored, silently — which is correct for a stray
    /// column and useless in a template.
    /// </summary>
    private static readonly HashSet<string> EmployeeFields =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "azureAdObjectId", "firstName", "lastName", "name", "displayName", "credentials",
            "email", "title", "officePhone", "mobilePhone", "extension", "officeLocation",
            "department", "departmentId", "contactType",
        };

    [Fact]
    public void EveryTemplateColumnIsAFieldTheParserReads()
    {
        foreach (var column in TemplateColumns)
        {
            var canonical = BulkImportService.CanonicalHeader(column);

            EmployeeFields.Should().Contain(canonical,
                $"template column '{column}' canonicalises to '{canonical}', which the parser never reads");
        }
    }

    [Fact]
    public void TemplateOffersDepartmentByName()
    {
        // The specific regression. An unrecognised department name fails the whole import,
        // so the column that takes a name has to be the one people are shown.
        TemplateColumns.Should().Contain("department");
        BulkImportService.CanonicalHeader("department").Should().Be("department");
    }

    [Fact]
    public void TemplateCarriesTheNumberACodeCallDials()
    {
        // CodeCallDispatchService resolves the on-call provider's mobile to send the alert
        // and records a hard dispatch failure when there is none on file.
        TemplateColumns.Should().Contain("mobilePhone");
        BulkImportService.CanonicalHeader("mobilePhone").Should().Be("mobilePhone");
    }

    [Fact]
    public void TemplateColumnsAreUnique()
    {
        TemplateColumns.Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// The headers a real HR or telecom export actually carries — spaced, title-cased, and
    /// worded for people rather than for us. These are what a user meets before they ever
    /// see our template, and the alias table is what saves them from renaming columns.
    /// </summary>
    [Theory]
    [InlineData("First Name", "firstName")]
    [InlineData("Last Name", "lastName")]
    [InlineData("Job Title", "title")]
    [InlineData("Desk Phone", "officePhone")]
    [InlineData("Mobile Number", "mobilePhone")]
    [InlineData("Work Email", "email")]
    [InlineData("E-mail", "email")]
    [InlineData("Dept", "department")]
    [InlineData("Cell", "mobilePhone")]
    [InlineData("first_name", "firstName")]
    [InlineData("FIRSTNAME", "firstName")]
    public void RealWorldHeadersReachTheRightField(string header, string expected)
    {
        BulkImportService.CanonicalHeader(header)
            .Should().BeEquivalentTo(expected);
    }

    /// <summary>
    /// A header that is already a field name must come back in *our* spelling, not the
    /// file's.
    ///
    /// This is what the import wizard compares against its field list to decide which
    /// option to select. It matched exactly, so "First Name" — which canonicalised to
    /// "FirstName" — matched nothing and the column was drawn as "Ignore this column".
    /// The parser was unaffected (its row lookups are case-insensitive), so the file
    /// imported correctly while the screen said those columns would be dropped. Email is
    /// required, which made it look like the import could not possibly work.
    /// </summary>
    [Theory]
    [InlineData("First Name", "firstName")]
    [InlineData("first name", "firstName")]
    [InlineData("LAST NAME", "lastName")]
    [InlineData("Email", "email")]
    [InlineData("EMAIL", "email")]
    [InlineData("Department", "department")]
    [InlineData("Display Name", "displayName")]
    [InlineData("Office Phone", "officePhone")]
    [InlineData("Mobile Phone", "mobilePhone")]
    [InlineData("Contact Type", "contactType")]
    [InlineData("Office Location", "officeLocation")]
    [InlineData("Department Id", "departmentId")]
    public void HeadersThatAreAlreadyFieldNamesComeBackCanonicallyCased(
        string header, string expected)
    {
        BulkImportService.CanonicalHeader(header)
            .Should().Be(expected, "the wizard matches this against its field list exactly");
    }

    [Fact]
    public void AnUnrecognisedHeaderIsReturnedUnchangedApartFromSeparators()
    {
        // "Employee ID" is not a field the employee parser reads, and must stay
        // unrecognised rather than being coerced onto something that looks close.
        BulkImportService.CanonicalHeader("Annual Salary").Should().Be("AnnualSalary");
    }
}
