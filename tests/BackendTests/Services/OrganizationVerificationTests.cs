using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// A code call reaches real clinicians and a schedule says who is responsible for
/// patients tonight, so an organization asserting it is a hospital gets one answer
/// checked against a source it does not control.
///
/// The most important test here is the grandfathering one. Every organization that
/// existed before this check did was already operating, and a gate that turned them all
/// read-only on deploy would take away schedules and code calls from live customers --
/// which is a worse outcome than never having built the gate.
/// </summary>
public class OrganizationVerificationTests
{
    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static OrganizationVerificationService CreateService(
        AppDbContext db, NpiLookup? lookup = null)
        => new(db, new StubRegistry(lookup), NullLogger<OrganizationVerificationService>.Instance);

    private static async Task<Tenant> AddTenantAsync(AppDbContext db)
    {
        var tenant = new Tenant { Name = "St. Elsewhere Hospital", IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    private static SubmitVerificationRequest Request(
        string type = OrganizationType.Hospital,
        string legalName = "St. Elsewhere Hospital",
        string? npi = "1234567893",
        string? email = "admin@stelsewhere.org",
        string? state = "NY")
        => new(type, legalName, null, npi, "1 Main St", "Kingston", state, "12401",
            "LIC-1", state, null, "Pat Reyes", "COO", email);

    private static NpiLookup Registered(
        string legalName = "ST ELSEWHERE HOSPITAL", string state = "NY", string type = "NPI-2")
        => new(true, true, legalName, type, "Kingston", state, "12401", null);

    // ── Grandfathering ──

    /// <summary>
    /// The property that keeps this change from being an outage. A tenant created before
    /// verification existed carries the model default, and the schema backport adds the
    /// column with the same default.
    /// </summary>
    [Fact]
    public async Task AnOrganizationThatPredatesThisCheckIsAlreadyVerified()
    {
        using var db = CreateDb();
        var tenant = await AddTenantAsync(db);

        tenant.VerificationStatus.Should().Be(VerificationStatus.Verified,
            "a deployment must not turn every live customer read-only");
    }

    // ── Who is in the flow at all ──

    /// <summary>
    /// CR-5 says somebody who only wants to manage a contact list signs up with no
    /// healthcare verification. If the gate applied to them it would block exactly the
    /// frictionless import path this is supposed to leave alone.
    /// </summary>
    [Fact]
    public async Task ANonHealthcareOrganizationIsVerifiedWithoutBeingChecked()
    {
        using var db = CreateDb();
        var tenant = await AddTenantAsync(db);

        var (verification, error) = await CreateService(db).SubmitAsync(
            tenant.Id, Request(type: OrganizationType.Other, npi: null, email: null), "Tester");

        error.Should().BeNull();
        verification.Should().BeNull("there is nothing to submit when there is nothing to verify");

        (await db.Tenants.SingleAsync()).VerificationStatus.Should().Be(VerificationStatus.Verified);
    }

    // ── The registry check ──

    [Fact]
    public async Task AMatchingNpiAndDomainVerifyOnTheirOwn()
    {
        using var db = CreateDb();
        var tenant = await AddTenantAsync(db);

        var (_, error) = await CreateService(db, Registered()).SubmitAsync(
            tenant.Id, Request(), "Tester");

        error.Should().BeNull();
        (await db.Tenants.SingleAsync()).VerificationStatus.Should().Be(VerificationStatus.Verified);
    }

    /// <summary>
    /// Registry entries are upper-cased and abbreviated in ways nobody types. A stricter
    /// comparison sends every real hospital to a human queue, which is the same as having
    /// no automatic check at all.
    /// </summary>
    [Theory]
    [InlineData("ST ELSEWHERE HOSPITAL")]
    [InlineData("St. Elsewhere Hospital, Inc.")]
    [InlineData("ST. ELSEWHERE HOSPITAL")]
    public async Task ANameWrittenDifferentlyStillAgrees(string registeredName)
    {
        using var db = CreateDb();
        var tenant = await AddTenantAsync(db);

        await CreateService(db, Registered(registeredName)).SubmitAsync(
            tenant.Id, Request(), "Tester");

        (await db.Tenants.SingleAsync()).VerificationStatus.Should().Be(VerificationStatus.Verified);
    }

    [Fact]
    public async Task ANameThatDoesNotMatchTheRegistryGoesToAPerson()
    {
        using var db = CreateDb();
        var tenant = await AddTenantAsync(db);

        await CreateService(db, Registered("ACME WIDGETS LLC")).SubmitAsync(
            tenant.Id, Request(), "Tester");

        (await db.Tenants.SingleAsync()).VerificationStatus.Should().Be(VerificationStatus.Pending);
    }

    [Fact]
    public async Task AnNpiNobodyHasRegisteredGoesToAPerson()
    {
        using var db = CreateDb();
        var tenant = await AddTenantAsync(db);

        var notFound = new NpiLookup(false, true, null, null, null, null, null,
            "No organization is registered under that NPI.");

        await CreateService(db, notFound).SubmitAsync(tenant.Id, Request(), "Tester");

        (await db.Tenants.SingleAsync()).VerificationStatus.Should().Be(VerificationStatus.Pending);
    }

    /// <summary>A Type 1 NPI belongs to an individual clinician, not to an organization.</summary>
    [Fact]
    public async Task AnIndividualsNpiIsNotEvidenceOfAnOrganization()
    {
        using var db = CreateDb();
        var tenant = await AddTenantAsync(db);

        await CreateService(db, Registered(type: "NPI-1")).SubmitAsync(
            tenant.Id, Request(), "Tester");

        var verification = await db.OrganizationVerifications.SingleAsync();
        verification.RegistryFindings.Should().Contain("individual");
        (await db.Tenants.SingleAsync()).VerificationStatus.Should().Be(VerificationStatus.Pending);
    }

    /// <summary>
    /// An outage is not evidence that a hospital does not exist. It must send the
    /// submission to a person, never reject it.
    /// </summary>
    [Fact]
    public async Task AnUnreachableRegistryWaitsForAPersonRatherThanRejecting()
    {
        using var db = CreateDb();
        var tenant = await AddTenantAsync(db);

        var down = new NpiLookup(false, false, null, null, null, null, null,
            "The NPI registry could not be reached.");

        await CreateService(db, down).SubmitAsync(tenant.Id, Request(), "Tester");

        var status = (await db.Tenants.SingleAsync()).VerificationStatus;
        status.Should().Be(VerificationStatus.Pending);
        status.Should().NotBe(VerificationStatus.Rejected,
            "CMS having a bad afternoon is not a finding about this hospital");
    }

    [Fact]
    public async Task AStateThatDisagreesWithTheRegistryGoesToAPerson()
    {
        using var db = CreateDb();
        var tenant = await AddTenantAsync(db);

        await CreateService(db, Registered(state: "CA")).SubmitAsync(
            tenant.Id, Request(state: "NY"), "Tester");

        (await db.Tenants.SingleAsync()).VerificationStatus.Should().Be(VerificationStatus.Pending);
    }

    // ── The representative's address ──

    [Theory]
    [InlineData("someone@gmail.com")]
    [InlineData("admin@yahoo.com")]
    [InlineData("chief@outlook.com")]
    public async Task APersonalEmailAddressNeverVerifiesOnItsOwn(string email)
    {
        using var db = CreateDb();
        var tenant = await AddTenantAsync(db);

        await CreateService(db, Registered()).SubmitAsync(
            tenant.Id, Request(email: email), "Tester");

        (await db.Tenants.SingleAsync()).VerificationStatus.Should().Be(VerificationStatus.Pending,
            "a personal address for a hospital is the cheapest evidence nobody has checked anything");
    }

    [Fact]
    public async Task ADomainUnrelatedToTheOrganizationGoesToAPerson()
    {
        using var db = CreateDb();
        var tenant = await AddTenantAsync(db);

        await CreateService(db, Registered()).SubmitAsync(
            tenant.Id, Request(email: "admin@acme-widgets.com"), "Tester");

        (await db.Tenants.SingleAsync()).VerificationStatus.Should().Be(VerificationStatus.Pending);
    }

    [Fact]
    public async Task NoRepresentativeAddressGoesToAPerson()
    {
        using var db = CreateDb();
        var tenant = await AddTenantAsync(db);

        await CreateService(db, Registered()).SubmitAsync(
            tenant.Id, Request(email: null), "Tester");

        (await db.Tenants.SingleAsync()).VerificationStatus.Should().Be(VerificationStatus.Pending);
    }

    // ── A person's decision ──

    [Fact]
    public async Task AnAdministratorCanApproveWhatTheChecksWouldNotPass()
    {
        using var db = CreateDb();
        var tenant = await AddTenantAsync(db);
        var service = CreateService(db, Registered("ACME WIDGETS LLC"));

        await service.SubmitAsync(tenant.Id, Request(), "Tester");
        (await db.Tenants.SingleAsync()).VerificationStatus.Should().Be(VerificationStatus.Pending);

        var (ok, error) = await service.DecideAsync(
            tenant.Id, VerificationStatus.Verified, "Confirmed by phone with the CMO.", "Admin");

        ok.Should().BeTrue(error);
        (await db.Tenants.SingleAsync()).VerificationStatus.Should().Be(VerificationStatus.Verified);

        var verification = await db.OrganizationVerifications.SingleAsync();
        verification.DecidedByName.Should().Be("Admin");
        verification.DecisionReason.Should().Be("Confirmed by phone with the CMO.");
    }

    [Fact]
    public async Task OnlyVerifiedOrRejectedAreDecisions()
    {
        using var db = CreateDb();
        var tenant = await AddTenantAsync(db);

        var (ok, error) = await CreateService(db).DecideAsync(
            tenant.Id, VerificationStatus.Pending, "why not", "Admin");

        ok.Should().BeFalse();
        error.Should().NotBeNull();
    }

    /// <summary>Resubmitting replaces the previous answer rather than accumulating.</summary>
    [Fact]
    public async Task ResubmittingReplacesTheEarlierSubmission()
    {
        using var db = CreateDb();
        var tenant = await AddTenantAsync(db);
        var service = CreateService(db, Registered());

        await service.SubmitAsync(tenant.Id, Request(email: "someone@gmail.com"), "Tester");
        await service.SubmitAsync(tenant.Id, Request(), "Tester");

        (await db.OrganizationVerifications.CountAsync()).Should().Be(1,
            "there must never be a question of which submission is current");
        (await db.Tenants.SingleAsync()).VerificationStatus.Should().Be(VerificationStatus.Verified);
    }

    private sealed class StubRegistry(NpiLookup? lookup) : INppesRegistryClient
    {
        public Task<NpiLookup> LookupAsync(string npi, CancellationToken ct = default) =>
            Task.FromResult(lookup ?? new NpiLookup(
                false, true, null, null, null, null, null, "No organization is registered under that NPI."));
    }
}
