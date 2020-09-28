﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace DExpSql.ExpressionHandle
{
    class ConstantExpressionCaluse : BaseExpressionSql<ConstantExpression>
    {
        protected override SqlCaluse Where(ConstantExpression exp, SqlCaluse sqlCaluse)
        {
            sqlCaluse += sqlCaluse.AddDbParameter(exp.Value);
            return sqlCaluse;
        }
    }
}
