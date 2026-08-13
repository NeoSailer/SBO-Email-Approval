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
for table in ['OWDD', 'WDD1']:
    print(table)
    cur.execute(f'SELECT TOP 1 * FROM {table}')
    print([desc[0] for desc in cur.description])
    print('')
cur.close()
conn.close()
