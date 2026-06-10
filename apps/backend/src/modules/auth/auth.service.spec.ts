import { Test, TestingModule } from '@nestjs/testing';
import { ConflictException, UnauthorizedException } from '@nestjs/common';
import * as bcrypt from 'bcryptjs';
import { AuthService } from './auth.service';
import { PrismaService } from '../../prisma/prisma.service';
import { MailService } from '../mail/mail.service';
import { AuthTokenService } from './auth-token.service';

describe('AuthService', () => {
  let service: AuthService;
  let prisma: jest.Mocked<PrismaService>;
  let mailService: jest.Mocked<MailService>;
  let tokenService: jest.Mocked<AuthTokenService>;

  const mockUser = {
    id: 'user-1',
    email: 'test@example.com',
    passwordHash: '',
    firstName: 'Test',
    lastName: 'User',
    role: 'OWNER' as const,
    emailVerified: false,
    isActive: true,
    organizationId: 'org-1',
    lastLoginAt: null,
    createdAt: new Date(),
    updatedAt: new Date(),
    deletedAt: null,
  };

  const mockOrganization = {
    id: 'org-1',
    name: 'Test Org',
    slug: 'test-org',
    createdAt: new Date(),
    updatedAt: new Date(),
    deletedAt: null,
  };

  beforeEach(async () => {
    mockUser.passwordHash = await bcrypt.hash('Password123', 4);

    const module: TestingModule = await Test.createTestingModule({
      providers: [
        AuthService,
        {
          provide: PrismaService,
          useValue: {
            organization: { findFirst: jest.fn(), create: jest.fn() },
            user: { findFirst: jest.fn(), create: jest.fn(), update: jest.fn() },
            emailVerification: { create: jest.fn(), deleteMany: jest.fn() },
            $transaction: jest.fn(),
          },
        },
        {
          provide: MailService,
          useValue: {
            sendVerificationEmail: jest.fn(),
            sendPasswordResetEmail: jest.fn(),
            sendTeamInvitationEmail: jest.fn(),
          },
        },
        {
          provide: AuthTokenService,
          useValue: {
            generateTokens: jest.fn().mockResolvedValue({
              accessToken: 'access-token',
              refreshToken: 'refresh-token',
              expiresIn: 900,
            }),
            revokeRefreshToken: jest.fn(),
            revokeAllUserTokens: jest.fn(),
          },
        },
      ],
    }).compile();

    service = module.get<AuthService>(AuthService);
    prisma = module.get(PrismaService);
    mailService = module.get(MailService);
    tokenService = module.get(AuthTokenService);
  });

  describe('register', () => {
    it('should register a new organization and user', async () => {
      (prisma.organization.findFirst as jest.Mock).mockResolvedValue(null);
      (prisma.$transaction as jest.Mock).mockImplementation(async (fn) =>
        fn({
          organization: { create: jest.fn().mockResolvedValue(mockOrganization) },
          user: { create: jest.fn().mockResolvedValue(mockUser) },
          emailVerification: { create: jest.fn() },
        }),
      );

      const result = await service.register({
        email: 'test@example.com',
        password: 'Password123',
        firstName: 'Test',
        lastName: 'User',
        organizationName: 'Test Org',
      });

      expect(result.user.email).toBe('test@example.com');
      expect(result.tokens.accessToken).toBe('access-token');
    });

    it('should throw ConflictException if organization slug exists', async () => {
      (prisma.organization.findFirst as jest.Mock).mockResolvedValue(mockOrganization);

      await expect(
        service.register({
          email: 'test@example.com',
          password: 'Password123',
          firstName: 'Test',
          lastName: 'User',
          organizationName: 'Test Org',
        }),
      ).rejects.toThrow(ConflictException);
    });
  });

  describe('login', () => {
    it('should login with valid credentials', async () => {
      (prisma.user.findFirst as jest.Mock).mockResolvedValue({
        ...mockUser,
        organization: mockOrganization,
      });
      (prisma.user.update as jest.Mock).mockResolvedValue(mockUser);

      const result = await service.login({
        email: 'test@example.com',
        password: 'Password123',
      });

      expect(result.user.email).toBe('test@example.com');
      expect(tokenService.generateTokens).toHaveBeenCalled();
    });

    it('should throw UnauthorizedException with invalid credentials', async () => {
      (prisma.user.findFirst as jest.Mock).mockResolvedValue(null);

      await expect(
        service.login({ email: 'wrong@example.com', password: 'wrong' }),
      ).rejects.toThrow(UnauthorizedException);
    });
  });
});
