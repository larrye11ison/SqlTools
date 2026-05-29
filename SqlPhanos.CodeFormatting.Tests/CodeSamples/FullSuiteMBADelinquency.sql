-- Scripting object: dbo.MbaDelinquencyStringCraziness
-- Type: SQL_STORED_PROCEDURE
-- Server: MEGABLERG
-- Database: ToolsTest
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER OFF
GO
CREATE OR ALTER PROC [dbo].[MbaDelinquencyStringCraziness]
AS
BEGIN
	DECLARE @backto DATE,
		@thru DATE;
	INSERT #MBADelqCalc
	WITH (TABLOCK) (
		LoanID,
		Data_Date,
		DueDate,
		MBADaysDelinquent,
		DelinquencyStringCode
	)
	SELECT
		ll.LoanID,
		ad.DATA_DATE,
		ad.DueDate,
		(
			((DATEPART(YYYY, ad.DATA_DATE) - DATEPART(YYYY, ad.DueDate)) * 360) + 
			((DATEPART(MM, ad.DATA_DATE) - DATEPART(MM, ad.DueDate)) * 30) + 
			(
		    	(
					CASE 
						WHEN ((DATEPART(MM, ad.DATA_DATE) = 2)
							AND (DATEPART(DD, ad.DATA_DATE) IN (28, 29))
						) THEN 30
						WHEN DATEPART(DD, ad.DATA_DATE) >= 30 THEN 30
						ELSE DATEPART(DD, ad.DATA_DATE)
					END
				) - 
				(
					CASE 
						WHEN DATEPART(DD, ad.DueDate) >= 30 THEN 30
						ELSE DATEPART(DD, ad.DueDate)
					END
				)
			)
		) + 1 AS MBADaysDelinquent,
		NULL AS DelinquencyStringCode
	FROM #LoanListDataTape ll
	JOIN Miser.dbo.ASSET_DETAIL_DAILY ad ON ll.LoanID = ad.LoanID
		AND ad.Data_Date BETWEEN @backTo AND @thru
	JOIN OperationalDatamart.dbo.D_Time dt ON dt.date_id = ad.DATA_DATE
		AND dt.IsMonthEnd = 1;
END
