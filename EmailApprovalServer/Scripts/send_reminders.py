import os
import smtplib
import xml.etree.ElementTree as ET
from email.mime.text import MIMEText
from email.utils import formataddr
from urllib.parse import quote

import hdbcli.dbapi as dbapi

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CONFIG_PATH = os.path.join(ROOT, 'Config', 'appsettings.xml')


def load_config():
    tree = ET.parse(CONFIG_PATH)
    root = tree.getroot()
    return {
        'smtp_host': root.findtext('./Mail/SmtpHost', default=''),
        'smtp_port': int(root.findtext('./Mail/SmtpPort', default='587')),
        'smtp_user': root.findtext('./Mail/SmtpUser', default=''),
        'smtp_password': root.findtext('./Mail/SmtpPassword', default=''),
        'from_address': root.findtext('./Mail/FromAddress', default=''),
        'subject': root.findtext('./Reminder/TemplateSubject', default='Pending approval reminder'),
        'body': root.findtext('./Reminder/TemplateBody', default='There are pending approvals requiring action.'),
        'hana_host': root.findtext('./Hana/Host', default=''),
        'hana_port': int(root.findtext('./Hana/Port', default='30015')),
        'hana_user': root.findtext('./Hana/User', default=''),
        'hana_password': root.findtext('./Hana/Password', default=''),
        'hana_schema': root.findtext('./Hana/Schema', default=''),
        'base_url': root.findtext('./General/BaseUrl', default='http://127.0.0.1:5050'),
        'hana_sql': root.findtext('./Hana/Sql', default='SELECT 1 FROM DUMMY'),
    }


def fetch_pending_rows(config):
    conn = dbapi.connect(
        address=config['hana_host'],
        port=config['hana_port'],
        user=config['hana_user'],
        password=config['hana_password'],
        currentSchema=config['hana_schema'],
    )
    cur = conn.cursor()
    cur.execute(config['hana_sql'])
    rows = cur.fetchall()
    cur.close()
    conn.close()
    return rows


def build_body(config, rows):
    if not rows:
        return None

    lines = [config['body'], '', 'Pending approvals:']
    for row in rows:
        request_id = row[0] if row else ''
        approver = row[1] if len(row) > 1 else ''
        status = row[2] if len(row) > 2 else ''
        draft_entry = row[3] if len(row) > 3 else ''
        approval_link = f"{config['base_url']}/approval/approve?requestId={quote(str(request_id))}&approver={quote(str(approver))}" if request_id else ''
        reject_link = f"{config['base_url']}/approval/reject?requestId={quote(str(request_id))}&approver={quote(str(approver))}" if request_id else ''
        lines.append(f"- RequestId: {request_id} | Approver: {approver} | Status: {status} | DraftEntry: {draft_entry}")
        lines.append(f"  Approve: {approval_link}")
        lines.append(f"  Reject: {reject_link}")
    return '\n'.join(lines)


def send_mail(config, rows):
    body = build_body(config, rows)
    if not body:
        print('No pending approvals found')
        return

    msg = MIMEText(body, 'plain', 'utf-8')
    msg['Subject'] = config['subject']
    msg['From'] = formataddr(("Approval Service", config['from_address']))
    msg['To'] = config['smtp_user']

    with smtplib.SMTP(config['smtp_host'], config['smtp_port']) as server:
        server.starttls()
        server.login(config['smtp_user'], config['smtp_password'])
        server.sendmail(config['from_address'], [config['smtp_user']], msg.as_string())

    print('Reminder email sent')


if __name__ == '__main__':
    config = load_config()
    rows = fetch_pending_rows(config)
    send_mail(config, rows)
