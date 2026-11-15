-- =============================================
-- Script de datos iniciales para PharmaGo
-- Ejecutar en SQL Server Management Studio
-- Base de datos: PharmaDb
-- =============================================

USE PharmaDb;
GO

-- =============================================
-- 1. ROLES (obligatorios para la aplicación)
-- Administrator, Employee, Owner son usados en AuthorizationFilter
-- =============================================
IF NOT EXISTS (SELECT 1 FROM Roles WHERE Name = 'Administrator')
    INSERT INTO Roles (Name) VALUES ('Administrator');
IF NOT EXISTS (SELECT 1 FROM Roles WHERE Name = 'Employee')
    INSERT INTO Roles (Name) VALUES ('Employee');
IF NOT EXISTS (SELECT 1 FROM Roles WHERE Name = 'Owner')
    INSERT INTO Roles (Name) VALUES ('Owner');
GO

-- =============================================
-- 2. UNIT MEASURES (para medicamentos)
-- =============================================
IF NOT EXISTS (SELECT 1 FROM UnitMeasures WHERE Name = 'Gramos')
    INSERT INTO UnitMeasures (Name, Deleted) VALUES ('Gramos', 0);
IF NOT EXISTS (SELECT 1 FROM UnitMeasures WHERE Name = 'Mililitros')
    INSERT INTO UnitMeasures (Name, Deleted) VALUES ('Mililitros', 0);
IF NOT EXISTS (SELECT 1 FROM UnitMeasures WHERE Name = 'Unidades')
    INSERT INTO UnitMeasures (Name, Deleted) VALUES ('Unidades', 0);
GO

-- =============================================
-- 3. PRESENTATIONS (para medicamentos)
-- =============================================
IF NOT EXISTS (SELECT 1 FROM Presentations WHERE Name = 'Tabletas')
    INSERT INTO Presentations (Name, Deleted) VALUES ('Tabletas', 0);
IF NOT EXISTS (SELECT 1 FROM Presentations WHERE Name = 'Capsulas')
    INSERT INTO Presentations (Name, Deleted) VALUES ('Capsulas', 0);
IF NOT EXISTS (SELECT 1 FROM Presentations WHERE Name = 'Jarabe')
    INSERT INTO Presentations (Name, Deleted) VALUES ('Jarabe', 0);
IF NOT EXISTS (SELECT 1 FROM Presentations WHERE Name = 'Crema')
    INSERT INTO Presentations (Name, Deleted) VALUES ('Crema', 0);
GO

-- =============================================
-- 4. FARMACIA DE EJEMPLO
-- =============================================
IF NOT EXISTS (SELECT 1 FROM Pharmacys WHERE Name = 'Farmacia Central')
    INSERT INTO Pharmacys (Name, Address) VALUES ('Farmacia Central', 'Av. 18 de Julio 1234, Montevideo');
GO

-- =============================================
-- 5. USUARIOS DE PRUEBA (con roles)
-- Password para todos: Abcdef12. (cumple regex: mayúscula, minúscula, número, especial, 8+ chars)
-- =============================================
DECLARE @AdminRoleId INT = (SELECT Id FROM Roles WHERE Name = 'Administrator');
DECLARE @EmployeeRoleId INT = (SELECT Id FROM Roles WHERE Name = 'Employee');
DECLARE @OwnerRoleId INT = (SELECT Id FROM Roles WHERE Name = 'Owner');
DECLARE @PharmacyId INT = (SELECT Id FROM Pharmacys WHERE Name = 'Farmacia Central');

-- Usuario Administrador (sin farmacia asignada, rol global)
IF NOT EXISTS (SELECT 1 FROM Users WHERE UserName = 'admin')
    INSERT INTO Users (UserName, Email, Password, Address, RegistrationDate, RoleId, PharmacyId)
    VALUES ('admin', 'admin@pharmago.com', 'Abcdef12.', 'Oficina Central', GETDATE(), @AdminRoleId, NULL);

-- Usuario Owner (dueño de farmacia)
IF NOT EXISTS (SELECT 1 FROM Users WHERE UserName = 'owner001')
    INSERT INTO Users (UserName, Email, Password, Address, RegistrationDate, RoleId, PharmacyId)
    VALUES ('owner001', 'owner@farmaciacentral.com', 'Abcdef12.', 'Av. Italia 500', GETDATE(), @OwnerRoleId, @PharmacyId);

-- Usuario Employee (empleado de farmacia)
IF NOT EXISTS (SELECT 1 FROM Users WHERE UserName = 'empleado01')
    INSERT INTO Users (UserName, Email, Password, Address, RegistrationDate, RoleId, PharmacyId)
    VALUES ('empleado01', 'empleado@farmaciacentral.com', 'Abcdef12.', 'Av. Brasil 200', GETDATE(), @EmployeeRoleId, @PharmacyId);
GO

-- =============================================
-- 6. INVITACIONES DE EJEMPLO (para registro de nuevos usuarios)
-- UserCode debe ser 6 dígitos
-- =============================================
IF EXISTS (SELECT 1 FROM Pharmacys WHERE Name = 'Farmacia Central') AND 
   EXISTS (SELECT 1 FROM Roles WHERE Name = 'Employee') AND
   NOT EXISTS (SELECT 1 FROM Invitations WHERE UserName = 'nuevo_empleado' AND IsActive = 1)
    INSERT INTO Invitations (UserName, UserCode, IsActive, Created, PharmacyId, RoleId)
    SELECT 'nuevo_empleado', '123456', 1, GETDATE(), 
           (SELECT Id FROM Pharmacys WHERE Name = 'Farmacia Central'),
           (SELECT Id FROM Roles WHERE Name = 'Employee');
GO

-- =============================================
-- 7. MEDICAMENTOS DE EJEMPLO
-- =============================================
IF EXISTS (SELECT 1 FROM Pharmacys WHERE Name = 'Farmacia Central')
BEGIN
    DECLARE @PharmacyId INT = (SELECT Id FROM Pharmacys WHERE Name = 'Farmacia Central');
    DECLARE @UnitGramos INT = (SELECT Id FROM UnitMeasures WHERE Name = 'Gramos');
    DECLARE @UnitMl INT = (SELECT Id FROM UnitMeasures WHERE Name = 'Mililitros');
    DECLARE @PresTabletas INT = (SELECT Id FROM Presentations WHERE Name = 'Tabletas');
    DECLARE @PresCapsulas INT = (SELECT Id FROM Presentations WHERE Name = 'Capsulas');
    DECLARE @PresJarabe INT = (SELECT Id FROM Presentations WHERE Name = 'Jarabe');

    IF NOT EXISTS (SELECT 1 FROM Drugs WHERE Code = 'PARA001') AND @UnitGramos IS NOT NULL AND @PresTabletas IS NOT NULL
        INSERT INTO Drugs (Code, Name, Symptom, Quantity, Price, Stock, Prescription, Deleted, UnitMeasureId, PresentationId, PharmacyId)
        VALUES ('PARA001', 'Paracetamol 500mg', 'Dolor y fiebre', 20, 150.00, 100, 0, 0, @UnitGramos, @PresTabletas, @PharmacyId);

    IF NOT EXISTS (SELECT 1 FROM Drugs WHERE Code = 'IBUP002') AND @UnitGramos IS NOT NULL AND @PresTabletas IS NOT NULL
        INSERT INTO Drugs (Code, Name, Symptom, Quantity, Price, Stock, Prescription, Deleted, UnitMeasureId, PresentationId, PharmacyId)
        VALUES ('IBUP002', 'Ibuprofeno 400mg', 'Dolor e inflamación', 30, 280.50, 80, 0, 0, @UnitGramos, @PresTabletas, @PharmacyId);

    IF NOT EXISTS (SELECT 1 FROM Drugs WHERE Code = 'AMOX003') AND @UnitMl IS NOT NULL AND @PresJarabe IS NOT NULL
        INSERT INTO Drugs (Code, Name, Symptom, Quantity, Price, Stock, Prescription, Deleted, UnitMeasureId, PresentationId, PharmacyId)
        VALUES ('AMOX003', 'Amoxicilina 250mg/5ml', 'Infecciones bacterianas', 60, 450.00, 50, 1, 0, @UnitMl, @PresJarabe, @PharmacyId);

    IF NOT EXISTS (SELECT 1 FROM Drugs WHERE Code = 'OMEP004') AND @UnitGramos IS NOT NULL AND @PresCapsulas IS NOT NULL
        INSERT INTO Drugs (Code, Name, Symptom, Quantity, Price, Stock, Prescription, Deleted, UnitMeasureId, PresentationId, PharmacyId)
        VALUES ('OMEP004', 'Omeprazol 20mg', 'Acidez estomacal', 14, 320.00, 120, 0, 0, @UnitGramos, @PresCapsulas, @PharmacyId);
END
GO

PRINT 'Datos iniciales insertados correctamente.';
PRINT 'Usuarios de prueba: admin, owner001, empleado01';
PRINT 'Password para todos: Abcdef12.';
PRINT '';
PRINT 'Roles: Administrator, Employee, Owner';
GO