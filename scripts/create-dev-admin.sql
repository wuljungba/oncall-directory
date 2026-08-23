-- Create TenantAdmin record for dev user (yisaland24@gmail.com)
-- Grants SuperAdmin access to the Test tenant

-- First, find the Test tenant's ID
DECLARE @TestTenantId INT;
SELECT TOP 1 @TestTenantId = Id FROM Tenants WHERE Name = 'Test';

IF @TestTenantId IS NULL
BEGIN
    PRINT 'ERROR: Test tenant not found. Check that the tenant exists.';
    THROW 50000, 'Test tenant not found', 1;
END

PRINT 'Found Test tenant with ID: ' + CAST(@TestTenantId AS VARCHAR(10));

-- Check if the TenantAdmin record already exists
IF EXISTS (SELECT 1 FROM TenantAdmins
           WHERE TenantId = @TestTenantId
           AND AzureAdObjectId = '00000000-0000-0000-0000-000000000001')
BEGIN
    PRINT 'TenantAdmin record already exists. Skipping insert.';
END
ELSE
BEGIN
    -- Insert the TenantAdmin record for the dev user
    INSERT INTO TenantAdmins (TenantId, AzureAdObjectId, Role, IsAutoAssigned, CreatedAt, LastSyncedAt)
    VALUES (
        @TestTenantId,
        '00000000-0000-0000-0000-000000000001',  -- dev user's Azure AD object ID
        'SuperAdmin',
        0,
        GETUTCDATE(),
        GETUTCDATE()
    );

    PRINT 'Successfully created TenantAdmin record:';
    PRINT '  - TenantId: ' + CAST(@TestTenantId AS VARCHAR(10));
    PRINT '  - AzureAdObjectId: 00000000-0000-0000-0000-000000000001';
    PRINT '  - Role: SuperAdmin';
    PRINT '  - IsAutoAssigned: 0 (manual assignment)';
    PRINT '';
    PRINT 'The dev user (yisaland24@gmail.com) now has SuperAdmin access to the Test tenant.';
    PRINT 'Refresh your browser to see the updated permissions.';
END
