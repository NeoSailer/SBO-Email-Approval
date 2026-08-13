import xml.etree.ElementTree as ET
import hdbcli.dbapi as dbapi

root = ET.parse('Config/appsettings.xml').getroot()
hana = root.find('Hana')
if hana is None:
    raise SystemExit('No Hana section in config')

host = hana.findtext('Host', default='')
port = int(hana.findtext('Port', default='30015'))
user = hana.findtext('User', default='')
password = hana.findtext('Password', default='')
schema = hana.findtext('Schema', default='')
sql = hana.findtext('Sql', default='SELECT 1 FROM DUMMY')

print('connecting', host, port, user, schema)
conn = dbapi.connect(address=host, port=port, user=user, password=password, currentSchema=schema)
cur = conn.cursor()
cur.execute(sql)
rows = cur.fetchall()
print('rows', len(rows))
for row in rows[:5]:
    print(row)
cur.close()
conn.close()
