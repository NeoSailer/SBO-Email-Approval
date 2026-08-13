import xml.etree.ElementTree as ET
import hdbcli.dbapi as dbapi

root = ET.parse('Config/appsettings.xml').getroot()
hana = root.find('Hana')
conn = dbapi.connect(
    address=hana.findtext('Host'),
    port=int(hana.findtext('Port', default='30015')),
    user=hana.findtext('User'),
    password=hana.findtext('Password'),
    currentSchema=hana.findtext('Schema'),
)
cur = conn.cursor()
cur.execute("SELECT TABLE_NAME FROM SYS.TABLES WHERE TABLE_NAME IN ('OWDD','WDD1')")
print(cur.fetchall())
cur.close()
conn.close()
