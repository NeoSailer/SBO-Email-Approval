import xml.etree.ElementTree as ET
from hdbcli import dbapi

root = ET.parse('Config/appsettings.xml').getroot()
h = root.find('Hana')
conn = dbapi.connect(
    address=h.findtext('Host'),
    port=int(h.findtext('Port')),
    user=h.findtext('User'),
    password=h.findtext('Password'),
    currentSchema=h.findtext('Schema'),
)
cur = conn.cursor()
cur.execute('SELECT TOP 20 "WddCode", "OwnerID", "Status", "Remarks" FROM "OWDD" ORDER BY "WddCode" DESC')
rows = cur.fetchall()
print('Total rows:', len(rows))
print('Sample rows:')
for r in rows:
    print(r)
cur.execute('SELECT COUNT(*) FROM "OWDD" WHERE "Status" = \'P\'')
print('P status count:', cur.fetchone()[0])
cur.close()
conn.close()
