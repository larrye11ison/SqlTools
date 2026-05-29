select one, two, three, coalesce(isnull(foo, ''), format(SaleDate, 'yyyyMMdd'))
from Loan l
inner join status s on l.loanid = s.loanid
left outer join core..asset a
    inner join core..core_user_asset cua on cua.core_asset_id = a.core_asset_id
                    and cua.core_user_id = 100321
    on a.asset_id = l.loanid
