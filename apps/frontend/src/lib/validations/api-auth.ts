import { z } from 'zod';
import { UserRole } from '@netwatch/shared';
import {
  loginSchema,
  registerSchema,
  forgotPasswordSchema,
  resetPasswordSchema,
  acceptInvitationSchema,
  passwordSchema,
} from '@/lib/validations/auth';

export { loginSchema, registerSchema, forgotPasswordSchema, resetPasswordSchema, acceptInvitationSchema };

const invitableRoles = [UserRole.ADMIN, UserRole.ENGINEER, UserRole.VIEWER] as const;

export const inviteMemberSchema = z.object({
  email: z.string().email('Invalid email address'),
  role: z.enum(invitableRoles, { message: 'Invalid role for invitation' }),
});

export const acceptInvitationApiSchema = z.object({
  token: z.string().min(1, 'Invitation token is required'),
  firstName: z.string().min(1, 'First name is required'),
  lastName: z.string().min(1, 'Last name is required'),
  password: passwordSchema,
});

export const resetPasswordApiSchema = z.object({
  token: z.string().min(1, 'Reset token is required'),
  password: passwordSchema,
});

export const updateMemberSchema = z.object({
  role: z.enum([UserRole.OWNER, UserRole.ADMIN, UserRole.ENGINEER, UserRole.VIEWER]).optional(),
  isActive: z.boolean().optional(),
});
