import { Test, TestingModule } from '@nestjs/testing';
import { INestApplication, ValidationPipe, VersioningType } from '@nestjs/common';
import * as request from 'supertest';
import { AppModule } from '../src/app.module';
import { PrismaService } from '../src/prisma/prisma.service';
import { API_PREFIX } from '@netwatch/shared';

describe('AuthController (e2e)', () => {
  let app: INestApplication;
  let prisma: PrismaService;

  beforeAll(async () => {
    const moduleFixture: TestingModule = await Test.createTestingModule({
      imports: [AppModule],
    }).compile();

    app = moduleFixture.createNestApplication();
    app.setGlobalPrefix(API_PREFIX);
    app.enableVersioning({ type: VersioningType.URI, defaultVersion: '1' });
    app.useGlobalPipes(
      new ValidationPipe({ whitelist: true, forbidNonWhitelisted: true, transform: true }),
    );
    await app.init();

    prisma = app.get(PrismaService);
  });

  afterAll(async () => {
    await app.close();
  });

  const basePath = `${API_PREFIX}/auth`;

  describe('POST /auth/register', () => {
    it('should reject invalid email', () => {
      return request(app.getHttpServer())
        .post(`${basePath}/register`)
        .send({
          email: 'invalid',
          password: 'Password123',
          firstName: 'Test',
          lastName: 'User',
          organizationName: 'Test Org',
        })
        .expect(400);
    });

    it('should reject weak password', () => {
      return request(app.getHttpServer())
        .post(`${basePath}/register`)
        .send({
          email: 'test@example.com',
          password: 'weak',
          firstName: 'Test',
          lastName: 'User',
          organizationName: 'Test Org',
        })
        .expect(400);
    });
  });

  describe('GET /health', () => {
    it('should return health status', () => {
      return request(app.getHttpServer())
        .get(`${API_PREFIX}/health`)
        .expect(200)
        .expect((res) => {
          expect(res.body.data).toHaveProperty('status');
        });
    });
  });
});
