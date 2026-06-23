import nodemailer from 'nodemailer';

function appUrl() {
  return process.env.APP_URL || process.env.NEXT_PUBLIC_APP_URL || 'http://localhost:3001';
}

async function sendMail(to: string, subject: string, html: string) {
  const host = process.env.SMTP_HOST;
  if (!host) {
    console.log(`[email] To: ${to} | ${subject}\n${html}`);
    return;
  }
  const transporter = nodemailer.createTransport({
    host,
    port: parseInt(process.env.SMTP_PORT || '587', 10),
    secure: process.env.SMTP_SECURE === 'true',
    auth: process.env.SMTP_USER
      ? { user: process.env.SMTP_USER, pass: process.env.SMTP_PASSWORD }
      : undefined,
  });
  await transporter.sendMail({
    from: process.env.SMTP_FROM || 'netwatch@localhost',
    to,
    subject,
    html,
  });
}

export async function sendPasswordResetEmail(email: string, token: string) {
  const link = `${appUrl()}/reset-password?token=${encodeURIComponent(token)}`;
  await sendMail(
    email,
    'Reset your NetWatch password',
    `<p>You requested a password reset for NetWatch.</p>
     <p><a href="${link}">Reset password</a></p>
     <p>This link expires in 1 hour. If you did not request this, ignore this email.</p>`,
  );
}

export async function sendTeamInvitationEmail(
  email: string,
  token: string,
  companyName: string,
  inviterName: string,
) {
  const link = `${appUrl()}/accept-invitation?token=${encodeURIComponent(token)}`;
  await sendMail(
    email,
    `You're invited to NetWatch — ${companyName}`,
    `<p>${inviterName} invited you to join <strong>${companyName}</strong> on NetWatch.</p>
     <p><a href="${link}">Accept invitation</a></p>
     <p>This invitation expires in 7 days.</p>`,
  );
}
