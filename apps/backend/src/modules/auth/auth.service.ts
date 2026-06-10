import {
  Injectable,
  ConflictException,
  UnauthorizedException,
  NotFoundException,
  BadRequestException,
} from '@nestjs/common';
import * as bcrypt from 'bcryptjs';
import { UserRole as PrismaUserRole } from '@prisma/client';
import {
  BCRYPT_ROUNDS,
  EMAIL_VERIFICATION_EXPIRES_HOURS,
  PASSWORD_RESET_EXPIRES_HOURS,
  INVITATION_EXPIRES_DAYS,
  slugify,
  AuthUser,
} from '@netwatch/shared';
import { PrismaService } from '../../prisma/prisma.service';
import { MailService } from '../mail/mail.service';
import { AuthTokenService } from './auth-token.service';
import { RegisterDto } from './dto/register.dto';
import { LoginDto } from './dto/login.dto';
import { InviteMemberDto } from './dto/invite-member.dto';
import { AcceptInvitationDto } from './dto/accept-invitation.dto';
import { generateSecureToken, getExpiresAt, getExpiresAtDays } from '../../common/utils/token.util';

@Injectable()
export class AuthService {
  constructor(
    private readonly prisma: PrismaService,
    private readonly mailService: MailService,
    private readonly tokenService: AuthTokenService,
  ) {}

  async register(dto: RegisterDto) {
    const slug = slugify(dto.organizationName);
    const existingOrg = await this.prisma.organization.findFirst({
      where: { slug, deletedAt: null },
    });

    if (existingOrg) {
      throw new ConflictException('Organization name is already taken');
    }

    const passwordHash = await bcrypt.hash(dto.password, BCRYPT_ROUNDS);

    const result = await this.prisma.$transaction(async (tx) => {
      const organization = await tx.organization.create({
        data: {
          name: dto.organizationName,
          slug,
        },
      });

      const user = await tx.user.create({
        data: {
          email: dto.email.toLowerCase(),
          passwordHash,
          firstName: dto.firstName,
          lastName: dto.lastName,
          role: PrismaUserRole.OWNER,
          organizationId: organization.id,
          emailVerified: true,
        },
      });

      return { user, organization };
    });

    const tokens = await this.tokenService.generateTokens(result.user);

    return {
      user: this.mapUser(result.user),
      organization: {
        id: result.organization.id,
        name: result.organization.name,
        slug: result.organization.slug,
      },
      tokens,
    };
  }

  async login(dto: LoginDto) {
    const user = await this.prisma.user.findFirst({
      where: {
        email: dto.email.toLowerCase(),
        deletedAt: null,
        isActive: true,
      },
      include: { organization: true },
    });

    if (!user) {
      throw new UnauthorizedException('Invalid credentials');
    }

    const isPasswordValid = await bcrypt.compare(dto.password, user.passwordHash);
    if (!isPasswordValid) {
      throw new UnauthorizedException('Invalid credentials');
    }

    await this.prisma.user.update({
      where: { id: user.id },
      data: { lastLoginAt: new Date() },
    });

    const tokens = await this.tokenService.generateTokens(user);

    return {
      user: this.mapUser(user),
      organization: {
        id: user.organization.id,
        name: user.organization.name,
        slug: user.organization.slug,
      },
      tokens,
    };
  }

  async logout(refreshToken: string) {
    if (refreshToken) {
      await this.tokenService.revokeRefreshToken(refreshToken);
    }
    return { message: 'Logged out successfully' };
  }

  async refreshTokens(refreshToken: string) {
    try {
      const tokens = await this.tokenService.refreshAccessToken(refreshToken);
      return { tokens };
    } catch {
      throw new UnauthorizedException('Invalid or expired refresh token');
    }
  }

  async verifyEmail(token: string) {
    const verification = await this.prisma.emailVerification.findUnique({
      where: { token },
      include: { user: true },
    });

    if (!verification || verification.expiresAt < new Date()) {
      throw new BadRequestException('Invalid or expired verification token');
    }

    await this.prisma.$transaction([
      this.prisma.user.update({
        where: { id: verification.userId },
        data: { emailVerified: true },
      }),
      this.prisma.emailVerification.delete({ where: { id: verification.id } }),
    ]);

    return { message: 'Email verified successfully' };
  }

  async resendVerificationEmail(userId: string) {
    const user = await this.prisma.user.findFirst({
      where: { id: userId, deletedAt: null },
    });

    if (!user) throw new NotFoundException('User not found');
    if (user.emailVerified) throw new BadRequestException('Email already verified');

    await this.prisma.emailVerification.deleteMany({ where: { userId } });

    const token = generateSecureToken();
    await this.prisma.emailVerification.create({
      data: {
        token,
        userId,
        expiresAt: getExpiresAt(EMAIL_VERIFICATION_EXPIRES_HOURS),
      },
    });

    await this.mailService.sendVerificationEmail(user.email, token);
    return { message: 'Verification email sent' };
  }

  async forgotPassword(email: string) {
    const user = await this.prisma.user.findFirst({
      where: { email: email.toLowerCase(), deletedAt: null, isActive: true },
    });

    if (user) {
      await this.prisma.passwordReset.deleteMany({ where: { userId: user.id } });

      const token = generateSecureToken();
      await this.prisma.passwordReset.create({
        data: {
          token,
          userId: user.id,
          expiresAt: getExpiresAt(PASSWORD_RESET_EXPIRES_HOURS),
        },
      });

      await this.mailService.sendPasswordResetEmail(user.email, token);
    }

    return { message: 'If an account exists, a password reset email has been sent' };
  }

  async resetPassword(token: string, password: string) {
    const reset = await this.prisma.passwordReset.findUnique({
      where: { token },
      include: { user: true },
    });

    if (!reset || reset.expiresAt < new Date() || reset.usedAt) {
      throw new BadRequestException('Invalid or expired reset token');
    }

    const passwordHash = await bcrypt.hash(password, BCRYPT_ROUNDS);

    await this.prisma.$transaction([
      this.prisma.user.update({
        where: { id: reset.userId },
        data: { passwordHash },
      }),
      this.prisma.passwordReset.update({
        where: { id: reset.id },
        data: { usedAt: new Date() },
      }),
    ]);

    await this.tokenService.revokeAllUserTokens(reset.userId);

    return { message: 'Password reset successfully' };
  }

  async inviteMember(organizationId: string, invitedById: string, dto: InviteMemberDto) {
    const inviter = await this.prisma.user.findFirst({
      where: { id: invitedById, organizationId, deletedAt: null },
      include: { organization: true },
    });

    if (!inviter) throw new NotFoundException('Inviter not found');

    if (dto.role === 'OWNER') {
      throw new BadRequestException('Cannot invite users as OWNER');
    }

    const existingUser = await this.prisma.user.findFirst({
      where: { email: dto.email.toLowerCase(), organizationId, deletedAt: null },
    });

    if (existingUser) {
      throw new ConflictException('User already exists in this organization');
    }

    const pendingInvite = await this.prisma.teamInvitation.findFirst({
      where: {
        email: dto.email.toLowerCase(),
        organizationId,
        status: 'PENDING',
        expiresAt: { gt: new Date() },
      },
    });

    if (pendingInvite) {
      throw new ConflictException('Invitation already pending for this email');
    }

    const token = generateSecureToken();
    const invitation = await this.prisma.teamInvitation.create({
      data: {
        email: dto.email.toLowerCase(),
        role: dto.role as PrismaUserRole,
        token,
        organizationId,
        invitedById,
        expiresAt: getExpiresAtDays(INVITATION_EXPIRES_DAYS),
      },
    });

    await this.mailService.sendTeamInvitationEmail(
      dto.email,
      token,
      inviter.organization.name,
      `${inviter.firstName} ${inviter.lastName}`,
    );

    return {
      id: invitation.id,
      email: invitation.email,
      role: invitation.role,
      expiresAt: invitation.expiresAt,
    };
  }

  async acceptInvitation(dto: AcceptInvitationDto) {
    const invitation = await this.prisma.teamInvitation.findUnique({
      where: { token: dto.token },
      include: { organization: true },
    });

    if (!invitation || invitation.status !== 'PENDING' || invitation.expiresAt < new Date()) {
      throw new BadRequestException('Invalid or expired invitation');
    }

    const passwordHash = await bcrypt.hash(dto.password, BCRYPT_ROUNDS);

    const result = await this.prisma.$transaction(async (tx) => {
      const user = await tx.user.create({
        data: {
          email: invitation.email,
          passwordHash,
          firstName: dto.firstName,
          lastName: dto.lastName,
          role: invitation.role,
          organizationId: invitation.organizationId,
          emailVerified: true,
        },
      });

      await tx.teamInvitation.update({
        where: { id: invitation.id },
        data: {
          status: 'ACCEPTED',
          acceptedById: user.id,
          acceptedAt: new Date(),
        },
      });

      return user;
    });

    const tokens = await this.tokenService.generateTokens(result);

    return {
      user: this.mapUser(result),
      organization: {
        id: invitation.organization.id,
        name: invitation.organization.name,
        slug: invitation.organization.slug,
      },
      tokens,
    };
  }

  async getProfile(userId: string) {
    const user = await this.prisma.user.findFirst({
      where: { id: userId, deletedAt: null },
      include: { organization: true },
    });

    if (!user) throw new NotFoundException('User not found');

    return {
      user: this.mapUser(user),
      organization: {
        id: user.organization.id,
        name: user.organization.name,
        slug: user.organization.slug,
      },
    };
  }

  private mapUser(user: {
    id: string;
    email: string;
    firstName: string;
    lastName: string;
    role: string;
    organizationId: string;
    emailVerified: boolean;
  }): AuthUser {
    return {
      id: user.id,
      email: user.email,
      firstName: user.firstName,
      lastName: user.lastName,
      role: user.role as AuthUser['role'],
      organizationId: user.organizationId,
      emailVerified: user.emailVerified,
    };
  }
}
