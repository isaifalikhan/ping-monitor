import { Injectable, Logger } from '@nestjs/common';
import { ConfigService } from '@nestjs/config';
import * as nodemailer from 'nodemailer';
import type { Transporter } from 'nodemailer';

export interface SendMailOptions {
  to: string;
  subject: string;
  html: string;
  text?: string;
}

@Injectable()
export class MailService {
  private readonly logger = new Logger(MailService.name);
  private transporter: Transporter | null = null;

  constructor(private readonly config: ConfigService) {
    const host = this.config.get<string>('SMTP_HOST');
    const user = this.config.get<string>('SMTP_USER');
    const pass = this.config.get<string>('SMTP_PASS');

    if (host && user && pass) {
      this.transporter = nodemailer.createTransport({
        host,
        port: this.config.get<number>('SMTP_PORT', 587),
        auth: { user, pass },
      });
    } else if (host) {
      this.logger.warn(
        'SMTP_HOST is set but SMTP_USER/SMTP_PASS are missing — emails will be logged only',
      );
    }
  }

  async sendMail(options: SendMailOptions): Promise<void> {
    const from = this.config.get<string>('SMTP_FROM', 'NetWatch <noreply@netwatch.io>');

    if (!this.transporter) {
      this.logger.warn(`Mail not configured. Would send to ${options.to}: ${options.subject}`);
      this.logger.debug(options.html);
      return;
    }

    try {
      await this.transporter.sendMail({
        from,
        to: options.to,
        subject: options.subject,
        html: options.html,
        text: options.text,
      });
    } catch (error) {
      this.logger.error(`Failed to send email to ${options.to}: ${(error as Error).message}`);
      if (this.config.get<string>('NODE_ENV') === 'production') {
        throw error;
      }
    }
  }

  async sendVerificationEmail(email: string, token: string): Promise<void> {
    const appUrl = this.config.get<string>('APP_URL', 'http://localhost:3000');
    const verifyUrl = `${appUrl}/verify-email?token=${token}`;

    await this.sendMail({
      to: email,
      subject: 'Verify your NetWatch email',
      html: `
        <h2>Welcome to NetWatch</h2>
        <p>Please verify your email address by clicking the link below:</p>
        <p><a href="${verifyUrl}">Verify Email</a></p>
        <p>This link expires in 24 hours.</p>
      `,
      text: `Verify your email: ${verifyUrl}`,
    });
  }

  async sendPasswordResetEmail(email: string, token: string): Promise<void> {
    const appUrl = this.config.get<string>('APP_URL', 'http://localhost:3000');
    const resetUrl = `${appUrl}/reset-password?token=${token}`;

    await this.sendMail({
      to: email,
      subject: 'Reset your NetWatch password',
      html: `
        <h2>Password Reset</h2>
        <p>Click the link below to reset your password:</p>
        <p><a href="${resetUrl}">Reset Password</a></p>
        <p>This link expires in 1 hour. If you didn't request this, ignore this email.</p>
      `,
      text: `Reset your password: ${resetUrl}`,
    });
  }

  async sendTeamInvitationEmail(
    email: string,
    token: string,
    organizationName: string,
    inviterName: string,
  ): Promise<void> {
    const appUrl = this.config.get<string>('APP_URL', 'http://localhost:3000');
    const inviteUrl = `${appUrl}/accept-invitation?token=${token}`;

    await this.sendMail({
      to: email,
      subject: `You've been invited to join ${organizationName} on NetWatch`,
      html: `
        <h2>Team Invitation</h2>
        <p>${inviterName} has invited you to join <strong>${organizationName}</strong> on NetWatch.</p>
        <p><a href="${inviteUrl}">Accept Invitation</a></p>
        <p>This invitation expires in 7 days.</p>
      `,
      text: `Accept invitation: ${inviteUrl}`,
    });
  }
}
