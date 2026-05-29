CREATE OR ALTER PROCEDURE dbo.usp_StressTest_Formatter_Parser
	@InputID INT,
	@JsonPayload NVARCHAR(MAX),
	@IsDebug BIT = 0,
	@OutputStatus NVARCHAR(250) OUTPUT
AS
BEGIN
	SET NOCOUNT ON;
	SET XACT_ABORT ON;
	DECLARE @CurrentStep NVARCHAR(50) = 'INIT';
	DECLARE @CalculatedThreshold DECIMAL(18,4);
	BEGIN TRY
		IF @InputID IS NOT NULL
			AND @InputID > 0
		BEGIN
			IF ISJSON(@JsonPayload) > 0
			BEGIN
				-- Deeply nested flow control with messy inline expressions
				IF EXISTS (
					SELECT 1
					FROM sys.objects
					WHERE name = 'TargetTable'
					AND type = 'U'
					)
				BEGIN
					SET @CurrentStep = 'PROCESSING';
					-- Deeply nested functions/parentheticals stress test
					SELECT @CalculatedThreshold = CONVERT(
						DECIMAL(18, 4),
						COALESCE(
							NULLIF(
								ISNULL(
									TRY_CAST( JSON_VALUE(@JsonPayload, '$.config.threshold') AS NUMERIC(10, 2)),
									TYPEPROPERTY(RTRIM(LTRIM(' decimal ')), 'Precision')
									),
								0
								),
							ABS(CHECKSUM(NEWID()) % 100) * 1.5, FORMAT(GETDATE(), 'yyyyMMdd')
							)
						);
					IF @CalculatedThreshold > 50.00
					BEGIN
						-- Multi-layered IF nesting with TRY/CATCH blocks inside
						BEGIN TRY
							IF @IsDebug = 1
							BEGIN
								PRINT 'Threshold exceeded: ' + CAST(@CalculatedThreshold AS VARCHAR(50));
							END
							ELSE
							BEGIN
								INSERT INTO dbo.LogTable(
									LogMsg,
									LogDate
								)
								VALUES(
									CONCAT(
										'Value: ', 
										NULLIF(COALESCE(CAST(@InputID AS VARCHAR(10)), 'UNK'), '0')
									),
									GETDATE()
								);
							END
						END TRY
						BEGIN CATCH
							-- Empty catch blocks or light error swallowing to test indentation
							SET @OutputStatus = 'INNER_ERROR: ' + ERROR_MESSAGE();
						END CATCH;
					END
					ELSE
					BEGIN
						SET @OutputStatus = 'THRESHOLD_MET';
					END
				END
				ELSE
				BEGIN
					SET @OutputStatus = 'MISSING_TARGET_TABLE';
				END
			END
			ELSE
			BEGIN
				SET @OutputStatus = 'INVALID_JSON';
			END
		END 
		ELSE
		BEGIN
			RAISERROR('Invalid Input ID provided.', 16, 1);
		END
		-- THE MONSTER QUERY: Deep expression nesting mixed with nested join geometry
		SELECT
			z.BaseID,
			f.FooCode,
			b.BarDescription,
			-- Deep parenthetical function stack
			COALESCE(
				NULLIF(
					RTRIM(
						LTRIM(
							ISNULL(UPPER(FORMAT(b.UpdatedDate, 'yyyy-MM-dd HH:mm:ss')), 'NOT_MODIFIED')
						)
					),
					''
				),
				UPPER(LEFT(ISNULL(f.FooName, 'UNKNOWN_FOO'), 3)), 'DEFAULT_FALLBACK'
			) AS ComplexStringExpression,
			-- CASE statement nesting inside math operators inside aggregation
			SUM(
				CASE
					WHEN z.IsActive = 1 THEN ISNULL(b.Weight, 0.0) * (1.0 + COALESCE(NULLIF(f.Multiplier, 0), 1.0))
					ELSE
					CASE 
						WHEN NULLIF(z.StatusCode, 'ERR') IS NULL THEN 0.0
						ELSE 1.0
					END
				END
			) AS AggregatedWeight
		FROM dbo.BazTable z 
		/* --- START OF NESTED JOIN GEOMETRY --- */
		LEFT OUTER JOIN dbo.FooTable f
				INNER JOIN dbo.BarTable b ON b.FooID = f.FooID
					AND b.IsCurrent = 1
					AND (
						b.StatusCode IN ('A', 'P')
						OR b.Flag = 
							CASE 
								WHEN f.Type = 'X' THEN 1
								ELSE 0
							END
					) 
			ON f.BaseID = z.BaseID
				AND f.PartitionDate >= CAST('2026-01-01' AS DATE) 
		/* --- END OF NESTED JOIN GEOMETRY --- */
		CROSS APPLY (
			SELECT TOP (1) ItemValue
			FROM dbo.SplitFunction(z.MetadataPipeDelimited, '|')
			WHERE ItemIndex = IIF(b.BarID IS NOT NULL, 1, ISNULL(f.PriorityIndex, 0))
			) ca
		WHERE z.CreatedDate <= GETDATE()
			AND (
			(
				z.TypeFlag = 'A'
			AND ( f.FooCode LIKE 'TX%'
			OR b.BarDescription IS NOT NULL)
				)
			OR (
				z.TypeFlag = 'B'
			AND NOT ( f.FooCode IS NULL
			AND b.BarID = (
				SELECT
					MIN(BarID)
				FROM dbo.BarTable))
				)
			)
		GROUP BY z.BaseID, f.FooCode, b.BarDescription, b.UpdatedDate, f.FooName, b.BarID, f.PriorityIndex, z.MetadataPipeDelimited;
SET @OutputStatus = 'SUCCESS';
END TRY
BEGIN CATCH
	-- Global error handler with formatted token blocks
	SELECT
		@OutputStatus = ERROR_MESSAGE(),
		@CurrentStep = 'FAILED_AT_' + @CurrentStep;
	INSERT INTO dbo.ErrorLog (
			ErrNum,
			ErrSev,
			ErrState,
			ErrProc,
			ErrLine,
			ErrMsg,
			StepContext
	)
	VALUES (
			ERROR_NUMBER(),
			ERROR_SEVERITY(),
			ERROR_STATE(),
			ERROR_PROCEDURE(),
			ERROR_LINE(),
			ERROR_MESSAGE(),
			@CurrentStep
	);
	THROW;
END CATCH;
END;
GO
