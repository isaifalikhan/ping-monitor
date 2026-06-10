import { ApiPropertyOptional } from '@nestjs/swagger';
import { IsInt, IsOptional, IsString, IsEmail, Min, Max } from 'class-validator';

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
}
