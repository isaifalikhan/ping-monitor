import { Module } from '@nestjs/common';
import { ConfigModule } from '@nestjs/config';
import { ThrottlerModule, ThrottlerGuard } from '@nestjs/throttler';
import { APP_GUARD } from '@nestjs/core';
import { PrismaModule } from './prisma/prisma.module';
import { AuthModule } from './modules/auth/auth.module';
import { OrganizationsModule } from './modules/organizations/organizations.module';
import { HealthModule } from './modules/health/health.module';
import { MailModule } from './modules/mail/mail.module';
import { MonitorsModule } from './modules/monitors/monitors.module';
import { IncidentsModule } from './modules/incidents/incidents.module';
import { AlertsModule } from './modules/alerts/alerts.module';
import { MaintenanceModule } from './modules/maintenance/maintenance.module';
import { ReportsModule } from './modules/reports/reports.module';
import { SettingsModule } from './modules/settings/settings.module';
import { ChecksModule } from './modules/checks/checks.module';
import { AuditModule } from './modules/audit/audit.module';
import { DashboardModule } from './modules/dashboard/dashboard.module';
import { StatusPageModule } from './modules/status-page/status-page.module';

@Module({
  imports: [
    ConfigModule.forRoot({ isGlobal: true, envFilePath: ['.env', '../../.env'] }),
    ThrottlerModule.forRoot([
      {
        ttl: parseInt(process.env.THROTTLE_TTL || '60', 10) * 1000,
        limit: parseInt(process.env.THROTTLE_LIMIT || '100', 10),
      },
    ]),
    PrismaModule,
    MailModule,
    AuthModule,
    OrganizationsModule,
    MonitorsModule,
    IncidentsModule,
    AlertsModule,
    MaintenanceModule,
    ReportsModule,
    SettingsModule,
    ChecksModule,
    AuditModule,
    DashboardModule,
    StatusPageModule,
    HealthModule,
  ],
  providers: [
    {
      provide: APP_GUARD,
      useClass: ThrottlerGuard,
    },
  ],
})
export class AppModule {}
