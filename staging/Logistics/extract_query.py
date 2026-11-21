import json
import re
from pathlib import Path

root = Path(__file__).parent
query = (root / 'ReportQuery.cs').read_text(encoding='utf-8-sig')
types = re.findall(r'new DocumentType\("([0-9]+)", "([A-Z0-9]+)"', (root / 'Query.cs').read_text(encoding='utf-8-sig'))
union = ' UNION ALL '.join(f'SELECT \'{code}\' AS "Type", "DocEntry", "DocTotal" FROM "{table}"' for code, table in types)
expr = query.split('public static readonly string Select =', 1)[1].split(';', 1)[0]
start = expr.index('String.Join(')
end = expr.index(' + ") F ', start)
expr = expr[:start] + json.dumps(union) + expr[end:]
expr = re.sub(r'//[^\n]*', '', expr)
literals = re.findall(r'"(?:\\.|[^"\\])*"', expr)
sql = ''.join(json.loads(value) for value in literals)
(root / 'ReportQuery.sql').write_text(sql, encoding='utf-8')
print(f'Extracted actual report SQL using {len(types)} document types')
