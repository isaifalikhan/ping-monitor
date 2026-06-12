import { ApiPropertyOptional } from '@nestjs/swagger';
import { IsInt, IsOptional, IsString, IsEmail, IsBoolean, IsObject, Min, Max } from 'class-validator';

export class UpdateSettingsDto {
  @ApiPropertyOptional()
  @IsOptional()
  @IsInt()
  @Min(100)
  @Max(60000)
  latencyThresholdMs?: number;

  @ApiPropertyOptional()
  @IsOptional()
  @IsEmail()
  notificationEmail?: string;

  @ApiPropertyOptional()
  @IsOptional()
  @IsString()
  timezone?: string;

  @ApiPropertyOptional()
  @IsOptional()
  @IsBoolean()
  enablePublicStatusPage?: boolean;

  @ApiPropertyOptional()
  @IsOptional()
  @IsObject()
  preferences?: Record<string, unknown>;
}
